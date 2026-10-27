using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Services;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Editor.EditorGame.Game;

namespace Stride.Roads.Editor.Experiments;

/// <summary>
/// Experiment (option 3): a road drawing button put into the scene editor's viewport toolbar from outside, by finding
/// the toolbar in the window's visual tree, without any change to the editor.
/// <para>
/// Its shortcoming, kept on purpose to compare: it depends on the editor's XAML as it is (the toolbar's Grid button,
/// named <c>ToggleGrid</c>); if that changes, the button silently isn't there.
/// </para>
/// </summary>
[EditorGameService(typeof(EntityHierarchyEditorController))]
public sealed class RoadDrawButtonInjector : EditorGameServiceBase
{
    /// <summary>How long it looks for the toolbar, which the editor may show after its services are made.</summary>
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    private readonly EntityHierarchyEditorViewModel editor;
    private EditorTool? tool;
    private ToggleButton? button;
    private DispatcherTimer? timer;
    private bool syncing;

    public RoadDrawButtonInjector(EntityHierarchyEditorViewModel editor)
    {
        this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    public override IEnumerable<Type> Dependencies => [typeof(RoadDrawService)];

    protected override Task<bool> Initialize(EditorServiceGame game)
    {
        tool = game.EditorServices.Get<RoadDrawService>()?.Tools[0];
        if (tool != null)
            editor.Dispatcher.InvokeAsync(StartLooking);
        return Task.FromResult(true);
    }

    public override async ValueTask DisposeAsync()
    {
        await editor.Dispatcher.InvokeAsync(() =>
        {
            timer?.Stop();
            if (button?.Parent is Panel panel)
                panel.Children.Remove(button);
            button = null;
        });
        await base.DisposeAsync();
    }

    private void StartLooking()
    {
        var started = DateTime.UtcNow;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (sender, e) =>
        {
            if (TryInject() || DateTime.UtcNow - started > GiveUpAfter)
                timer.Stop();
        };
        timer.Start();
    }

    /// <summary>Puts the button before this editor's Grid button, if the toolbar is showing. Whether it did.</summary>
    private bool TryInject()
    {
        var grid = Application.Current?.Windows.Cast<Window>()
            .SelectMany(w => Descendants(w).OfType<ToggleButton>())
            .FirstOrDefault(b => b.Name == "ToggleGrid" && ReferenceEquals(b.DataContext, editor));
        if (grid?.Parent is not Panel panel)
            return false;

        button = new ToggleButton
        {
            Width = 28,
            Height = 28,
            Background = Brushes.Transparent,
            ToolTip = $"{tool!.DisplayName} (injected experiment)",
            Content = Icon(tool.Icon) is { } icon
                ? new Image { Source = icon, Width = 16, Height = 16 }
                : new TextBlock { Text = "R", VerticalAlignment = VerticalAlignment.Center },
            IsChecked = tool.IsActive,
        };
        button.Checked += (sender, e) => Toggled(true);
        button.Unchecked += (sender, e) => Toggled(false);
        tool.ActiveChanged += (sender, e) => editor.Dispatcher.InvokeAsync(() =>
        {
            if (button == null)
                return;
            syncing = true;
            button.IsChecked = tool.IsActive;
            syncing = false;
        });
        panel.Children.Insert(panel.Children.IndexOf(grid), button);
        return true;
    }

    private void Toggled(bool active)
    {
        if (!syncing)
            tool!.SetActive(active);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }

    private static ImageSource? Icon(byte[]? data)
    {
        if (data == null)
            return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(data);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
