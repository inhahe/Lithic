// Why this harness exists
// ------------------------
// Expanding a drive in Lithic's source-selection TreeView takes seconds, while
// the filesystem work behind it takes ~10 ms (measured: 249 dirs + 39 files
// under D:\ enumerate in 6 ms, attributes in 4 ms). So the cost is in WPF
// layout, not in I/O -- but "which part of the layout" is a guess until it is
// measured, and guessing is what produced two wrong diagnoses already today.
//
// Two structural suspects in SourceSelectionView.xaml, both of which this
// harness isolates by building the same item template three ways:
//
//   A  StackPanel root + SharedSizeGroup   -- what the app ships today
//   B  Grid root + SharedSizeGroup         -- suspect 1 removed
//   C  Grid root, no shared sizing         -- both removed
//
// Suspect 1: the TreeViewItem ControlTemplate's root is a <StackPanel>, and a
// vertical StackPanel measures its children with infinite height. The nested
// ItemsPresenter therefore never learns a viewport, so its VirtualizingStackPanel
// realizes every child -- IsVirtualizing="True" on the TreeView is silently a
// no-op. The stock WPF TreeViewItem template uses a Grid with rows Auto/* for
// exactly this reason.
//
// Suspect 2: every row's Grid puts four of its five columns in SharedSizeGroups
// inside one Grid.IsSharedSizeScope. Every participant in a scope must be
// measured together, so with virtualization defeated the per-row cost scales
// with the number of rows.
//
// The harness also measures a second, separate stall the user sees *after* the
// rows appear: the directory-size scheduler streams results in one at a time,
// and each one changes a value in the shared "SizeCol" group.
//
// Run:  dotnet run --project tools\treeview_bench -c Release [-- <childCount>]

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace TreeViewBench;

/// <summary>Stand-in for SourceSelectionNodeViewModel: only the properties the
/// item template actually binds to, so the layout work is representative.</summary>
public sealed class Node : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public int Depth { get; set; }
    public ObservableCollection<Node> Children { get; } = new();

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Raise(nameof(IsExpanded)); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Raise(nameof(IsSelected)); }
    }

    private bool _autoIncludeNew;
    public bool AutoIncludeNew
    {
        get => _autoIncludeNew;
        set { _autoIncludeNew = value; Raise(nameof(AutoIncludeNew)); }
    }

    private string _formattedSize = "Working...";
    public string FormattedSize
    {
        get => _formattedSize;
        set { _formattedSize = value; Raise(nameof(FormattedSize)); }
    }

    private string _formattedFileCount = "";
    public string FormattedFileCount
    {
        get => _formattedFileCount;
        set { _formattedFileCount = value; Raise(nameof(FormattedFileCount)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public static class Program
{
    private const string Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string NsX = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Build the window XAML for one variant. Everything except the two things
    /// under test is identical across variants, so a difference in the numbers
    /// can only come from the root panel or the shared sizing.
    /// </summary>
    private static string BuildXaml(bool gridRoot, bool sharedSizing)
    {
        // Suspect 1. A StackPanel hands its child infinite height; a Grid whose
        // second row is star-sized hands it the remaining (finite) height, which
        // is what lets TreeViewItem negotiate a viewport with its ItemsHost.
        string rootOpen = gridRoot
            ? "<Grid><Grid.RowDefinitions><RowDefinition Height=\"Auto\" />" +
              "<RowDefinition /></Grid.RowDefinitions>"
            : "<StackPanel>";
        string rootClose = gridRoot ? "</Grid>" : "</StackPanel>";
        string rowAttr = gridRoot ? "Grid.Row=\"0\"" : "";
        string itemsAttr = gridRoot ? "Grid.Row=\"1\"" : "";

        // Suspect 2.
        string ssg(string group) => sharedSizing ? $" SharedSizeGroup=\"{group}\"" : "";
        string scope = sharedSizing ? "True" : "False";

        return $$"""
<Window xmlns="{{Ns}}" xmlns:x="{{NsX}}"
        Width="900" Height="600"
        Title="TreeView bench"
        WindowStartupLocation="Manual" Left="-2000" Top="-2000">
  <Border x:Name="Scope" Grid.IsSharedSizeScope="{{scope}}">
    <TreeView x:Name="Tree"
              BorderThickness="0"
              VirtualizingStackPanel.IsVirtualizing="True"
              VirtualizingStackPanel.VirtualizationMode="Recycling"
              VirtualizingPanel.ScrollUnit="Pixel">
      <TreeView.ItemContainerStyle>
        <Style TargetType="TreeViewItem">
          <Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}" />
          <Setter Property="Margin" Value="0" />
          <Setter Property="Padding" Value="0" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="TreeViewItem">
                {{rootOpen}}
                  <Grid MinHeight="24" {{rowAttr}}>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width="Auto"{{ssg("AutoIncludeCol")}} />
                      <ColumnDefinition Width="Auto"{{ssg("IncludeCol")}} />
                      <ColumnDefinition Width="*" />
                      <ColumnDefinition Width="Auto" MinWidth="56"{{ssg("FilesCol")}} />
                      <ColumnDefinition Width="Auto" MinWidth="120"{{ssg("SizeCol")}} />
                    </Grid.ColumnDefinitions>
                    <Border Grid.Column="0" BorderBrush="#333" BorderThickness="0,0,1,0">
                      <CheckBox IsChecked="{Binding AutoIncludeNew, Mode=TwoWay}"
                                VerticalAlignment="Center" HorizontalAlignment="Center" />
                    </Border>
                    <Border Grid.Column="1" BorderBrush="#333" BorderThickness="0,0,1,0">
                      <CheckBox IsChecked="{Binding IsSelected, Mode=TwoWay}"
                                IsThreeState="False"
                                VerticalAlignment="Center" HorizontalAlignment="Center" />
                    </Border>
                    <Border x:Name="NameArea" Grid.Column="2" Background="Transparent"
                            BorderBrush="#333" BorderThickness="0,0,1,0">
                      <StackPanel Orientation="Horizontal" Margin="16,0,0,0"
                                  VerticalAlignment="Center">
                        <Border x:Name="ExpanderIcon" Width="16" Height="16" Margin="0,0,2,0">
                          <Path x:Name="Arrow" Data="M 4 2 L 12 6 L 4 10 Z" Fill="#888"
                                VerticalAlignment="Center" HorizontalAlignment="Center"
                                RenderTransformOrigin="0.5,0.5">
                            <Path.RenderTransform><RotateTransform Angle="0" /></Path.RenderTransform>
                          </Path>
                        </Border>
                        <ContentPresenter x:Name="PART_Header" ContentSource="Header"
                                          VerticalAlignment="Center" />
                        <Border x:Name="StatusDotArea" Background="Transparent" Padding="3"
                                Margin="2,0,0,0" VerticalAlignment="Center">
                          <Ellipse x:Name="StatusDot" Width="7" Height="7" Fill="#2E7D32" />
                        </Border>
                      </StackPanel>
                    </Border>
                    <Border Grid.Column="3" BorderBrush="#333" BorderThickness="0,0,1,0">
                      <TextBlock Text="{Binding FormattedFileCount}" VerticalAlignment="Center"
                                 TextAlignment="Right" FontSize="11" Padding="8,0,8,0" />
                    </Border>
                    <Border Grid.Column="4">
                      <TextBlock Text="{Binding FormattedSize}" VerticalAlignment="Center"
                                 TextAlignment="Right" FontSize="11" Padding="8,0,8,0" />
                    </Border>
                  </Grid>
                  <ItemsPresenter x:Name="ItemsHost" {{itemsAttr}} Visibility="Collapsed" />
                {{rootClose}}
                <ControlTemplate.Triggers>
                  <Trigger Property="IsExpanded" Value="True">
                    <Setter TargetName="ItemsHost" Property="Visibility" Value="Visible" />
                    <Setter TargetName="Arrow" Property="RenderTransform">
                      <Setter.Value><RotateTransform Angle="90" /></Setter.Value>
                    </Setter>
                  </Trigger>
                  <Trigger Property="HasItems" Value="False">
                    <Setter TargetName="ExpanderIcon" Property="Visibility" Value="Hidden" />
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
      </TreeView.ItemContainerStyle>
      <TreeView.ItemTemplate>
        <HierarchicalDataTemplate ItemsSource="{Binding Children}">
          <TextBlock Text="{Binding Name}" VerticalAlignment="Center" />
        </HierarchicalDataTemplate>
      </TreeView.ItemTemplate>
    </TreeView>
  </Border>
</Window>
""";
    }

    private static List<Node> BuildModel(int childCount)
    {
        var root = new Node { Name = "D:\\", IsDirectory = true, Depth = 0 };
        for (int i = 0; i < childCount; i++)
        {
            var child = new Node
            {
                Name = $"child directory {i:D3} with a name of realistic length",
                IsDirectory = true,
                Depth = 1,
                FormattedFileCount = "—",
            };
            // Grandchildren exist so the child rows are expandable, exactly as
            // real directory rows are -- an item with no children is a different
            // (cheaper) case in the container generator.
            for (int j = 0; j < 3; j++)
                child.Children.Add(new Node { Name = $"g{j}", Depth = 2 });
            root.Children.Add(child);
        }
        return new List<Node> { root };
    }

    /// <summary>Let the dispatcher run everything queued at Loaded priority and
    /// above -- i.e. finish measuring, arranging and rendering.</summary>
    private static void Drain(DispatcherPriority priority = DispatcherPriority.Loaded)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static int CountVisual<T>(DependencyObject root) where T : DependencyObject
    {
        int n = root is T ? 1 : 0;
        int c = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < c; i++)
            n += CountVisual<T>(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    private readonly record struct Result(double ExpandMs, int Realized, double SizesMs);

    private static Result RunVariant(bool gridRoot, bool sharedSizing, int childCount)
    {
        // Layout timings are easily swamped by a gen-2 collection landing mid-run,
        // which is what made an early version of this harness report that the
        // *fastest* variant was the slowest. Collect deliberately first, and take
        // the minimum over several rounds rather than a single sample.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var window = (Window)XamlReader.Parse(BuildXaml(gridRoot, sharedSizing));
        var tree = (TreeView)window.FindName("Tree");
        var model = BuildModel(childCount);
        tree.ItemsSource = model;

        window.Show();
        Drain();

        // --- Phase 1: the expansion the user is waiting on -----------------
        var sw = Stopwatch.StartNew();
        model[0].IsExpanded = true;
        window.UpdateLayout();
        Drain();                     // includes the render pass
        sw.Stop();
        double expandMs = sw.Elapsed.TotalMilliseconds;

        int realized = CountVisual<TreeViewItem>(window);

        // --- Phase 2: directory sizes streaming in afterwards ---------------
        // SubmitDirectorySizeComputation hands every child to the background
        // scheduler; each result rewrites FormattedSize, which is bound into the
        // shared "SizeCol" column. Feed them in one at a time, as the scheduler
        // does, and let layout settle between each.
        sw.Restart();
        foreach (var child in model[0].Children)
        {
            child.FormattedSize = "12.3 GB";
            window.UpdateLayout();
        }
        Drain();
        sw.Stop();
        double sizesMs = sw.Elapsed.TotalMilliseconds;

        window.Close();
        Drain();

        return new Result(expandMs, realized, sizesMs);
    }

    [STAThread]
    public static void Main(string[] args)
    {
        int childCount = args.Length > 0
            ? int.Parse(args[0], CultureInfo.InvariantCulture)
            : 249;                                  // what D:\ actually contains

        Console.OutputEncoding = Encoding.UTF8;
        // OnExplicitShutdown, not the default OnLastWindowClose: closing the
        // first variant's window would otherwise shut the Application down and
        // every later variant would silently measure nothing.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        Console.WriteLine($"expanding one node with {childCount} children");

        var variants = new (string Label, bool GridRoot, bool Shared)[]
        {
            ("A  StackPanel root + shared sizing (today)", false, true),
            ("B  Grid root + shared sizing",               true,  true),
            ("C  Grid root, no shared sizing",             true,  false),
            ("D  StackPanel root, no shared sizing",       false, false),
        };

        const int rounds = 4;
        var best = new Dictionary<string, Result>();

        // Round 0 is a throwaway: it pays for JIT, font loading and theme
        // resource resolution, all of which would otherwise be charged to
        // whichever variant happens to run first.
        for (int round = 0; round <= rounds; round++)
        {
            foreach (var (label, gridRoot, shared) in variants)
            {
                var r = RunVariant(gridRoot, shared, round == 0 ? 16 : childCount);
                if (round == 0) continue;
                if (!best.TryGetValue(label, out var prev) || r.ExpandMs < prev.ExpandMs)
                    best[label] = r;
                else if (r.SizesMs < prev.SizesMs)
                    best[label] = prev with { SizesMs = r.SizesMs };
            }
        }

        Console.WriteLine($"best of {rounds} rounds:\n");
        foreach (var (label, _, _) in variants)
        {
            var r = best[label];
            Console.WriteLine($"{label,-44} expand {r.ExpandMs,8:N1} ms   " +
                              $"realized {r.Realized,5}   sizes-in {r.SizesMs,8:N1} ms");
        }

        app.Shutdown();
    }
}
