using System.Diagnostics;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;

namespace WorldsYetUnseen.Client;

internal static class DesktopSmoke
{
    internal static string WindowTitle { get; } = $"Worlds Yet Unseen D3D12 Smoke ({Environment.ProcessId})";

    internal static void RequestNativeClose(in RendererSnapshot presented, bool terrain)
    {
        using var process = Process.GetCurrentProcess();
        if (!string.Equals(process.MainWindowTitle, WindowTitle, StringComparison.Ordinal) || !process.CloseMainWindow())
            throw new InvalidOperationException("Could not send native close to the WYU smoke window.");

        Console.WriteLine($"D3D12 presented {(terrain ? "terrain" : "cube")} frame {presented.LastPresentedSequence} at " +
            $"{presented.AppliedWidth}x{presented.AppliedHeight}; native close requested.");
    }

    internal static void VerifyShutdown(D3D12Renderer renderer, bool requireDebugLayer)
    {
        using var process = Process.GetCurrentProcess();
        if (process.MainWindowHandle != IntPtr.Zero)
            throw new InvalidOperationException("The WYU smoke process still owns a main window.");

        var diagnostics = renderer.ReadDiagnostics();
        if (diagnostics.Adapter is not { IsSoftware: false } adapter || diagnostics.DeviceRemoval is not null)
            throw new InvalidOperationException("The D3D12 smoke did not settle a healthy hardware adapter.");

        Console.WriteLine($"D3D12 adapter: {adapter.Description} ({adapter.VendorId:X4}:{adapter.DeviceId:X4}), " +
            $"driver {adapter.DriverVersion ?? "unavailable"}; software={adapter.IsSoftware}.");
        Console.WriteLine($"D3D12 debug layer={diagnostics.DebugLayerEnabled}; DRED={diagnostics.DredEnabled}.");
        if (requireDebugLayer && (!diagnostics.DebugLayerEnabled || !diagnostics.DredEnabled))
            throw new InvalidOperationException("The terrain smoke requires an active D3D12 debug layer and DRED.");

        if (diagnostics.DebugLayerEnabled)
        {
            var report = diagnostics.DebugMessages;
            Console.WriteLine($"D3D12 live-object report: warnings={report?.Warnings}, errors={report?.Errors}, " +
                $"corruption={report?.Corruption}, discarded={report?.Discarded}, " +
                $"HRESULT=0x{report?.LiveObjectReportResult:X8}, live child objects={report?.LiveChildObjects}.");
            if (report is not { Errors: 0, Corruption: 0, Discarded: 0, LiveObjectReportResult: 0, LiveChildObjects: 0 })
                throw new InvalidOperationException("The active D3D12 debug layer did not report clean device settlement.");
        }

        Console.WriteLine("D3D12 renderer stopped; native window closed; Core resources released.");
    }
}
