using GeBar.App;
using GeBar.Core;
using GeBar.Dpi;
using GeBar.UI;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;

namespace GeBar.Tests;

public class DpiModuleTests
{
    [Fact]
    public void BuildComponentAppearsOnceWithAnyInstalledModule()
    {
        Assert.Empty(ControlCenterWindow.ModuleEntries(null, null));
        foreach ((string? hid, string? dpi, string[] expected) in new (string?, string?, string[])[]
        {
            ("hid-version", null, ["g-eHID hid-version", "ModuleImpact 10.300.1"]),
            (null, "dpi-version", ["g-eDPI dpi-version", "ModuleImpact 10.300.1"]),
            ("hid-version", "dpi-version", ["g-eHID hid-version", "g-eDPI dpi-version", "ModuleImpact 10.300.1"])
        })
        {
            IReadOnlyList<ControlCenterWindow.ModuleInfo> entries = ControlCenterWindow.ModuleEntries(hid, dpi);
            Assert.Equal(expected, entries.Select(entry => entry.Text));
            Assert.Equal(GeBar.App.Strings.ModuleImpactHint, entries[^1].ToolTip);
            Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.ToolTip)));
        }
    }

    [Fact]
    public void OptionalModuleLoadsWithoutLockingItsFileAndUsesSharedUiContract() => Sta(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-dpi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "g-eDPI.dll");
            File.Copy(typeof(DpiModule).Assembly.Location, path);
            using var loader = new SettingsModuleLoader();
            loader.Load(path, new FileLogger(directory, "test"));

            ISettingsModule module = Assert.IsAssignableFrom<ISettingsModule>(loader.Module);
            Assert.Equal("g-eDPI", module.Name);
            Assert.Contains("-dpi-", module.Version);
            Assert.NotEqual(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(module.GetType().Assembly));
            File.Move(path, path + ".old");
            Assert.True(File.Exists(path + ".old"));
            module.SetDeviceAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            Assert.False(module.CanSave);
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public void ModuleViewReusesProjectThemeAndHasNoIndependentSaveButton() => Sta(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), $"gebar-dpi-ui-{Guid.NewGuid():N}");
        try
        {
            using var module = new DpiModule(new FileLogger(directory, "test"));
            var window = new Window { Width = 700, Height = 548, Content = module.View };
            try
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = AppTheme.ThemeSource });
                AppTheme.Apply(window.Resources);
                window.Measure(new Size(700, 548));
                window.Arrange(new Rect(0, 0, 700, 548));
                window.UpdateLayout();
                DependencyObject[] elements = [.. Descendants(module.View)];

                Assert.Same(window.FindResource("PageScroll"), ((ScrollViewer)module.View).Style);
                Border[] cards = [.. elements.OfType<Border>().Where(border => ReferenceEquals(border.Style, window.FindResource("Card")))];
                Assert.Equal(3, cards.Length);
                Assert.InRange(Math.Abs(cards[0].ActualWidth - cards[1].ActualWidth), 0, 1);
                Assert.InRange(Math.Abs(cards[0].ActualHeight - cards[1].ActualHeight), 0, 1);
                Assert.Same(window.FindResource("NumberBox"), Assert.Single(elements.OfType<TextBox>()).Style);
                Assert.Equal(4, elements.OfType<ComboBox>().Count());
                TextBox dpi = elements.OfType<TextBox>().Single();
                foreach (ComboBox combo in elements.OfType<ComboBox>().Take(2))
                {
                    double dpiRight = dpi.TranslatePoint(new(dpi.ActualWidth, 0), module.View).X;
                    double comboRight = combo.TranslatePoint(new(combo.ActualWidth, 0), module.View).X;
                    Assert.InRange(Math.Abs(dpiRight - comboRight), 0, 1);
                }
                Assert.Empty(elements.OfType<Button>());
                Assert.IsType<Button>(module.Actions);
                Assert.False(module.CanSave);
            }
            finally { window.Close(); }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public void ComboBoxItemsKeepAlignmentWhenOpenedAndReplaced() => Sta(() =>
    {
        using var messages = new StringWriter();
        using var listener = new TextWriterTraceListener(messages);
        TraceSource bindings = PresentationTraceSources.DataBindingSource;
        SourceLevels previous = bindings.Switch.Level;
        bindings.Switch.Level = SourceLevels.Error;
        bindings.Listeners.Add(listener);
        var combo = new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
        var window = new Window { Content = combo, Width = 240, Height = 100, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = AppTheme.ThemeSource });
            window.Show();
            combo.IsDropDownOpen = true;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var item = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.False(System.Windows.Data.BindingOperations.IsDataBound(item, Control.HorizontalContentAlignmentProperty));
            Assert.False(System.Windows.Data.BindingOperations.IsDataBound(item, Control.VerticalContentAlignmentProperty));
            combo.ItemsSource = new[] { "Third", "Fourth" };
            combo.SelectedIndex = 1;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            combo.IsDropDownOpen = false;
            window.Content = null;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.DoesNotContain(messages.ToString().Split('\n'), line => line.Contains("ComboBoxItem", StringComparison.Ordinal)
                && (line.Contains("HorizontalContentAlignment", StringComparison.Ordinal) || line.Contains("VerticalContentAlignment", StringComparison.Ordinal)));
        }
        finally
        {
            window.Close();
            bindings.Listeners.Remove(listener);
            bindings.Switch.Level = previous;
        }
    });

    [Fact]
    public void BatteryProtocolSupportDoesNotEnableUnknownSettingsWrites()
    {
        var driver = new WlMouseSettingsDriver(new TestLogger());
        var unsupported = new HidDeviceProfile("other", "Other mouse", 0xFFFF, 0xFFFF, null, null, [], new());
        var huan = unsupported with { VendorId = 0x36A7, ProductId = 0xA864 };

        Assert.False(driver.Supports(unsupported));
        Assert.True(driver.Supports(huan));
        Assert.True(driver.Supports(huan with { ProductId = 0xA863 }));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object value in LogicalTreeHelper.GetChildren(root))
        {
            if (value is not DependencyObject child) continue;
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

}
