using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace LithicBackup.Infrastructure.FileSystem;

/// <summary>
/// The background service flushing a volume on behalf of a process that may not
/// (<see cref="DurableFlush.TryFlushVolume"/> needs an elevated process, and the
/// app is usually not; the service runs as LocalSystem).
/// </summary>
/// <remarks>
/// <para>
/// One request per connection: the client sends a volume's name
/// (<see cref="DurableFlush.VolumeOf"/>; a drive letter would not do, the
/// service's logon session not seeing the user's subst and network drives) as one
/// line; the server flushes that volume and answers with one line, <c>OK</c>, or
/// <c>NOTPERMITTED</c> / <c>FAILED</c> followed by the reason
/// (<see cref="DurableFlush.Outcome"/>).
/// </para>
/// <para>
/// The pipe is open to every authenticated local user and closed to the network.
/// What it lets them do is flush a volume, which writes nothing that was not
/// already going to be written; anything but a volume's name is refused. They
/// may not add instances of their own to it. A process that took the name while
/// the service was not running could answer "OK" without flushing: that costs
/// durability, not data, and whoever can run it can already do worse to a
/// destination they can write to. The client lets the server identify it, not
/// act as it.
/// </para>
/// </remarks>
public static class VolumeFlushPipe
{
    /// <summary>The service's pipe, <c>\\.\pipe\LithicBackup.FlushVolume</c>.</summary>
    public const string Name = "LithicBackup.FlushVolume";

    /// <summary>How long a client waits for the service to answer the door.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a client waits for the flush. Writing out a volume's dirty data
    /// can take seconds after heavy writing to a slow disk.
    /// </summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Ask the service to flush <paramref name="volume"/>.
    /// </summary>
    /// <param name="volume">The volume, as <see cref="DurableFlush.VolumeOf"/> names it.</param>
    /// <param name="pipeName">The pipe; another than <see cref="Name"/> only in tests.</param>
    /// <returns>
    /// The service's outcome and its reason; or no outcome, and why, when no
    /// service answered: none is running, or it did not reply in time.
    /// </returns>
    public static async Task<(DurableFlush.Outcome? Outcome, string? Error)> RequestAsync(
        string volume, CancellationToken ct, string pipeName = Name)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(ConnectTimeout, ct).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            await writer.WriteLineAsync(volume.AsMemory(), ct).ConfigureAwait(false);

            using var replyTime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            replyTime.CancelAfter(ReplyTimeout);
            string? reply = await reader.ReadLineAsync(replyTime.Token).ConfigureAwait(false);
            return Parse(reply);
        }
        catch (TimeoutException)
        {
            return (null, "the background service is not running");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "the background service did not answer");
        }
        catch (IOException ex)
        {
            return (null, $"the background service could not be asked: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (null, $"the background service could not be asked: {ex.Message}");
        }
    }

    /// <summary>
    /// Serve flush requests until <paramref name="ct"/> is cancelled. Each
    /// connection is answered on its own task while the next one is awaited.
    /// </summary>
    /// <param name="onError">Told of a server that could not be set up, or a request that failed.</param>
    /// <param name="pipeName">The pipe; another than <see cref="Name"/> only in tests.</param>
    public static async Task ServeAsync(CancellationToken ct, Action<Exception>? onError = null, string pipeName = Name)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                onError?.Invoke(ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return;
                continue;
            }
            _ = Task.Run(() => AnswerAsync(server, onError, ct), CancellationToken.None);
        }
    }

    /// <summary>Read one request, flush, answer, hang up.</summary>
    private static async Task AnswerAsync(NamedPipeServerStream server, Action<Exception>? onError, CancellationToken ct)
    {
        await using (server)
        {
            try
            {
                using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };
                using var requestTime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTime.CancelAfter(TimeSpan.FromSeconds(10));
                string? volume = await reader.ReadLineAsync(requestTime.Token).ConfigureAwait(false);

                // TryFlushVolume refuses anything but a volume's name.
                var outcome = DurableFlush.TryFlushVolume(volume ?? "", out string? error);
                string reply = outcome == DurableFlush.Outcome.Flushed
                    ? "OK"
                    : $"{outcome.ToString().ToUpperInvariant()} {OneLine(error)}";
                await writer.WriteLineAsync(reply.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onError?.Invoke(ex);
            }
        }
    }

    /// <summary>An answer read back into an outcome; a garbled one is a failure.</summary>
    private static (DurableFlush.Outcome? Outcome, string? Error) Parse(string? reply)
    {
        if (reply is null)
            return (null, "the background service hung up without answering");
        if (reply == "OK")
            return (DurableFlush.Outcome.Flushed, null);
        int space = reply.IndexOf(' ');
        string word = space < 0 ? reply : reply[..space];
        string? why = space < 0 ? null : reply[(space + 1)..];
        return Enum.TryParse(word, ignoreCase: true, out DurableFlush.Outcome outcome)
            ? (outcome, why)
            : (DurableFlush.Outcome.Failed, $"the background service answered \"{reply}\"");
    }

    private static string OneLine(string? text) => (text ?? "").Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// Every authenticated local user may ask (<see cref="PipeAccessRights.ReadWrite"/>
    /// does not take in creating instances); nobody over the network; SYSTEM,
    /// administrators and the account serving it own it. The last is SYSTEM in
    /// the service, and must be named for a server in a test, which needs to
    /// create every instance after the first.
    /// </summary>
    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        using var self = WindowsIdentity.GetCurrent();
        if (self.User is not null)
            security.AddAccessRule(new PipeAccessRule(self.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
