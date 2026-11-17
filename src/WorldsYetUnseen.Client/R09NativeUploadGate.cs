using System.Diagnostics;
using System.Reflection;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;

namespace WorldsYetUnseen.Client;

// The pinned Groundwork package uses this hook just before native terrain preparation.
// Attach only to renderers created for the R09 qualification route.
internal sealed class R09NativeUploadGate
{
    private UploadHold? _armed;

    private R09NativeUploadGate()
    {
    }

    internal static R09NativeUploadGate Attach(D3D12Renderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (renderer.Read().Lifecycle != RendererLifecycle.Created)
            throw new InvalidOperationException("Attach the R09 native upload gate before renderer initialization.");

        const string rendererNamespace = "Groundwork.Rendering.D3D12.";
        var rendererType = typeof(D3D12Renderer);
        var assembly = rendererType.Assembly;
        var hookType = assembly.GetType(rendererNamespace + "GpuRetirementTestHooks");
        var gpuType = assembly.GetType(rendererNamespace + "GpuDevice");
        var field = rendererType.GetField("_retirementHooks",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (hookType is null || gpuType is null || field is null || !field.IsInitOnly || field.FieldType != hookType)
            throw BindingMismatch();
        if (field.GetValue(renderer) is not null)
            throw new InvalidOperationException("The renderer already has native hooks.");

        var constructors = hookType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length != 1)
            throw BindingMismatch();
        var constructor = constructors[0];
        var parameters = constructor.GetParameters();
        var callbackType = typeof(Action<>).MakeGenericType(gpuType);
        var callbackIndex = Array.FindIndex(parameters, parameter => parameter.Name == "BeforeVolumeUpload");
        if (callbackIndex < 0 || parameters[callbackIndex].ParameterType != callbackType ||
            parameters.Any(parameter => !parameter.HasDefaultValue || parameter.DefaultValue is not null))
            throw BindingMismatch();

        var gate = new R09NativeUploadGate();
        Action<object> beforeUpload = gate.BeforeVolumeUpload;
        var arguments = new object?[parameters.Length];
        arguments[callbackIndex] = Delegate.CreateDelegate(callbackType, beforeUpload.Target, beforeUpload.Method);
        var hooks = constructor.Invoke(arguments);
        field.SetValue(renderer, hooks);
        if (!ReferenceEquals(field.GetValue(renderer), hooks))
            throw BindingMismatch();
        return gate;
    }

    internal UploadHold Arm()
    {
        var hold = new UploadHold(this);
        if (Interlocked.CompareExchange(ref _armed, hold, null) is not null)
            throw new InvalidOperationException("An R09 native upload hold is already armed.");
        return hold;
    }

    private void BeforeVolumeUpload(object _) => Volatile.Read(ref _armed)?.WaitAtUploadEntry();

    private static InvalidOperationException BindingMismatch() =>
        new("The pinned Groundwork renderer no longer matches the R09 native upload hook.");

    internal sealed class UploadHold(R09NativeUploadGate gate) : IDisposable
    {
        private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(8);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal long Release()
        {
            if (!Entered.IsCompletedSuccessfully)
                throw new InvalidOperationException("The renderer has not entered the R09 native upload hold.");

            var releasedAt = Stopwatch.GetTimestamp();
            if (!_released.TrySetResult())
                throw new InvalidOperationException("The R09 native upload hold is no longer active.");
            return releasedAt;
        }

        public void Dispose()
        {
            _entered.TrySetCanceled();
            _released.TrySetResult();
            Interlocked.CompareExchange(ref gate._armed, null, this);
        }

        internal void WaitAtUploadEntry()
        {
            if (_entered.TrySetResult() && !_released.Task.Wait(MaximumWait) && _released.TrySetCanceled())
                throw new TimeoutException("The R09 qualification did not release the native terrain upload.");
        }
    }
}
