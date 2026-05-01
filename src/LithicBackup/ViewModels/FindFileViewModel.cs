using System.Collections.ObjectModel;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the "Find File" cross-backup-set search feature.
/// Lets users search for a file or directory by name substring and see
/// which backup sets contain matches, with per-set summary info.
/// </summary>
public class FindFileViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;

    private string _searchText = "";
    private string _statusText = "Enter a file or directory name to search across all backup sets.";
    private bool _isSearching;
    private bool _hasResults;

    /// <summary>Fired when the user clicks "Close".</summary>
    public event Action? DoneRequested;

    /// <summary>Fired when the user wants to restore from a specific backup set.</summary>
    public event Action<int>? RestoreRequested;

    public FindFileViewModel(ICatalogRepository catalog)
    {
        _catalog = catalog;
        Results = [];

        SearchCommand = new RelayCommand(_ => _ = SearchAsync(), _ => CanSearch());
        CloseCommand = new RelayCommand(_ => DoneRequested?.Invoke());
        RestoreFromSetCommand = new RelayCommand(
            param =>
            {
                if (param is int setId)
                    RestoreRequested?.Invoke(setId);
            });
    }

    // --- Properties ---

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    public bool HasResults
    {
        get => _hasResults;
        set => SetProperty(ref _hasResults, value);
    }

    public ObservableCollection<FileSearchResultViewModel> Results { get; }

    // --- Commands ---

    public ICommand SearchCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RestoreFromSetCommand { get; }

    // --- Logic ---

    private bool CanSearch() => !IsSearching && !string.IsNullOrWhiteSpace(SearchText);

    private async Task SearchAsync()
    {
        if (!CanSearch()) return;

        IsSearching = true;
        StatusText = "Searching...";
        Results.Clear();
        HasResults = false;

        try
        {
            var results = await _catalog.SearchFilesAcrossSetsAsync(SearchText.Trim());

            foreach (var result in results)
                Results.Add(new FileSearchResultViewModel(result));

            HasResults = Results.Count > 0;

            StatusText = Results.Count switch
            {
                0 => "No matching files found in any backup set.",
                1 => "Found matches in 1 backup set.",
                _ => $"Found matches in {Results.Count} backup sets.",
            };
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }
}

/// <summary>
/// ViewModel for a single search result row (one per backup set).
/// </summary>
public class FileSearchResultViewModel : ViewModelBase
{
    public FileSearchResultViewModel(FileSearchResult result)
    {
        Result = result;
    }

    public FileSearchResult Result { get; }

    public int BackupSetId => Result.BackupSetId;
    public string BackupSetName => Result.BackupSetName;
    public int MatchingFileCount => Result.MatchingFileCount;
    public string TotalSizeText => FormatBytes(Result.TotalSizeBytes);
    public int LatestVersion => Result.LatestVersion;
    public string LastBackedUpText => Result.LastBackedUpUtc?.ToString("yyyy-MM-dd HH:mm") ?? "N/A";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:N0} {units[i]}" : $"{size:N1} {units[i]}";
    }
}
