using GeBar.Core;
using GeBar.Hid;
using GeBar.HidApp;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace GeBar.Tests;

public class HidProfileFileTests
{
    private static HidDeviceProfile Device(string id) => new(id, id, 1, 2, null, null, [], new());

    [Fact]
    public void PacketDisplayKeepsUppercaseHexAndSpaces() => WithWindow((window, _) =>
    {
        Invoke<object?>(window, "PacketReceived", new ReadOnlyMemory<byte>([0, 0x0A, 0xFF]), null, 0);
        Invoke<object?>(window, "PacketReceived", ReadOnlyMemory<byte>.Empty, null, 0);
        Invoke<object?>(window, "FlushPackets");

        string text = ((TextBox)window.FindName("PacketsText")).Text;
        Assert.Contains("RX [3] 00 0A FF" + Environment.NewLine, text);
        Assert.Contains("RX [0] " + Environment.NewLine, text);
    });

    [Fact]
    public void CaptureBatchesAreBoundedAndStoppedReadersCannotAppend() => WithWindow((window, _) =>
    {
        for (int index = 0; index < 5000; index++)
            Invoke<object?>(window, "PacketReceived", new ReadOnlyMemory<byte>([0, (byte)(index >> 8), (byte)index]), null, 0);
        var pending = (System.Collections.ICollection)typeof(MainWindow).GetField("_pendingPackets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Assert.Equal(512, pending.Count);
        var text = (TextBox)window.FindName("PacketsText");
        Assert.Empty(text.Text);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Contains("skipped 4488 packets", text.Text);
        Assert.Contains("RX [3] 00 13 87", text.Text);
        Assert.Equal(513, text.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Empty(pending);

        Invoke<object?>(window, "PacketReceived", new ReadOnlyMemory<byte>([0, 0xEE]), null, 0);
        Invoke<Task>(window, "StopCaptureAsync").GetAwaiter().GetResult();
        string stopped = text.Text;
        Invoke<object?>(window, "PacketReceived", new ReadOnlyMemory<byte>([0, 0xFF]), null, 0);
        Invoke<object?>(window, "FlushPackets");
        Assert.Equal(stopped, text.Text);
        Invoke<object?>(window, "PacketReceived", new ReadOnlyMemory<byte>([0, 0xAA]), null, 1);
        Invoke<object?>(window, "FlushPackets");
        Assert.Contains("RX [2] 00 AA", text.Text);
    });

    [Fact]
    public void RenamedDocumentUpdatesAndRemovesOnlyTheSelectedDevice() => WithWindow((window, directory) =>
    {
        string path = Path.Combine(directory, "renamed.json");
        var original = new HidProfileExport(2, DateTimeOffset.Now, [Device("first"), Device("second")])
        {
            Dev = JsonNode.Parse("{\"preserved\":true}")!.AsObject()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(original));
        Assert.Equal(2, Invoke<HidDeviceProfile[]>(window, "LoadInstalledProfiles").Length);
        Assert.Equal(path, Invoke<string>(window, "GetProfilePath", Device("first")));

        Invoke<Task>(window, "WriteProfileAsync", path, Device("first") with { Label = "Edited" }, false).GetAwaiter().GetResult();
        HidProfileExport edited = Read(path);
        Assert.Equal(2, edited.Devices.Count);
        Assert.Equal("Edited", edited.Devices[0].Label);
        Assert.False(File.Exists(Path.Combine(directory, "first.json")));

        Invoke<Task>(window, "RemoveProfileAsync", Device("first")).GetAwaiter().GetResult();
        Assert.Equal("second", Assert.Single(Read(path).Devices).Id);
        Assert.True(JsonNode.DeepEquals(original.Dev, Read(path).Dev));

        Invoke<Task>(window, "WriteProfileAsync", path, Device("second") with { Label = "Still here" }, false).GetAwaiter().GetResult();
        Assert.Equal("Still here", Assert.Single(Read(path).Devices).Label);
        Invoke<Task>(window, "RemoveProfileAsync", Device("second")).GetAwaiter().GetResult();
        Assert.False(File.Exists(path));
    });

    [Fact]
    public void RemovingMergedProfileFromTheSameFileKeepsOtherDevices() => WithWindow((window, directory) =>
    {
        string path = Path.Combine(directory, "devices.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new HidProfileExport(2, DateTimeOffset.Now,
            [Device("primary"), Device("secondary"), Device("other")])));
        Invoke<HidDeviceProfile[]>(window, "LoadInstalledProfiles");
        HidDeviceProfile merged = Device("primary") with { Connections = [new(3, 4, null, null, [], new())] };

        Invoke<Task>(window, "WriteProfileAsync", path, merged, false).GetAwaiter().GetResult();
        Invoke<Task>(window, "RemoveProfileAsync", Device("secondary")).GetAwaiter().GetResult();

        Assert.Equal(["primary", "other"], Read(path).Devices.Select(device => device.Id));
        Assert.Single(Read(path).Devices[0].Connections!);
    });

    [Fact]
    public void ExportDoesNotChangeTheInstalledDocumentPath() => WithWindow((window, directory) =>
    {
        string installed = Path.Combine(directory, "renamed.json");
        string exported = Path.Combine(directory, "..", "exported.json");
        File.WriteAllText(installed, JsonSerializer.Serialize(new HidProfileExport(2, DateTimeOffset.Now, [Device("mouse")])));
        Invoke<HidDeviceProfile[]>(window, "LoadInstalledProfiles");

        Invoke<Task>(window, "WriteProfileAsync", exported, Device("mouse"), true).GetAwaiter().GetResult();

        Assert.Equal(installed, Invoke<string>(window, "GetProfilePath", Device("mouse")));
        Invoke<Task>(window, "RemoveProfileAsync", Device("mouse")).GetAwaiter().GetResult();
        Assert.False(File.Exists(installed));
        Assert.True(File.Exists(exported));
    });

    [Fact]
    public void UnsafeImportedIdCannotReachTheSettingsFile() => WithWindow((window, directory) =>
    {
        string settings = Path.GetFullPath(Path.Combine(directory, "..", "settings.json"));
        File.WriteAllText(settings, "keep");
        HidDeviceProfile malicious = Device("../settings");
        File.WriteAllText(Path.Combine(directory, "import.json"), JsonSerializer.Serialize(new HidProfileExport(2, DateTimeOffset.Now, [malicious])));

        Assert.Empty(Invoke<HidDeviceProfile[]>(window, "LoadInstalledProfiles"));
        TargetInvocationException error = Assert.Throws<TargetInvocationException>(() => Invoke<string>(window, "GetProfilePath", malicious));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal("keep", File.ReadAllText(settings));
    });

    [Fact]
    public void NewerDocumentIsNotDeleted() => WithWindow((window, directory) =>
    {
        string path = Path.Combine(directory, "mouse.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new HidProfileExport(3, DateTimeOffset.Now, [Device("mouse")])));

        Assert.Throws<InvalidDataException>(() => Invoke<Task>(window, "RemoveProfileAsync", Device("mouse")).GetAwaiter().GetResult());
        Assert.Equal(3, Read(path).FormatVersion);
    });

    [Fact]
    public void AutomaticInterfaceSelectionKeepsCaptureAndManualSelectionStopsIt() => WithWindow((window, _) =>
    {
        Type itemType = typeof(MainWindow).GetNestedType("InterfaceItem", BindingFlags.NonPublic)!;
        object Item(string path, int length) => Activator.CreateInstance(itemType, [new HidInterfaceProfile(path, length, 0, 0), 1, null, true])!;
        object first = Item("first", 0), second = Item("second", 65);
        var list = (ListBox)typeof(MainWindow).GetField("InterfacesList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window)!;
        list.ItemsSource = new[] { first, second };
        list.SelectedItem = first;
        var listeners = (List<HidCaptureSession>)typeof(MainWindow).GetField("_listeners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        listeners.Add(new HidCaptureSession("unopened"));

        typeof(MainWindow).GetMethod("SelectCaptureInterface", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [Item("second", 65)]);

        Assert.Same(second, list.SelectedItem);
        Assert.Single(listeners);
        var button = (Button)typeof(MainWindow).GetField("CaptureButton", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(window)!;
        Assert.True(button.IsEnabled);
        typeof(MainWindow).GetMethod("CaptureClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [button, new System.Windows.RoutedEventArgs()]);
        Assert.Empty(listeners);
        listeners.Add(new HidCaptureSession("unopened"));
        list.SelectedItem = first;
        Assert.Empty(listeners);
    });

    private static T Invoke<T>(MainWindow window, string method, params object?[] arguments) =>
        (T)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments)!;

    private static HidProfileExport Read(string path) => JsonSerializer.Deserialize<HidProfileExport>(File.ReadAllText(path))!;

    private static void WithWindow(Action<MainWindow, string> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), $"gebar-profile-files-{Guid.NewGuid():N}");
            string directory = Path.Combine(root, "Profiles");
            Directory.CreateDirectory(directory);
            MainWindow? window = null;
            try
            {
                window = new MainWindow(new FileLogger(root, "test"), directory);
                test(window, directory);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                window?.Close();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
