using Orleans;
using Orleans.Concurrency;
using Orleans.Hosting;
using Orleans.Reminders.Concurrency;
using Orleans.Runtime;

namespace Documentation.Grains.StatelessWorkers.Basic
{
    // <image_worker>
public interface IImageWorker : IGrainWithStringKey
{
    Task<byte[]> Resize(byte[] image, int width);
}

[StatelessWorker]
public sealed class ImageWorkerGrain : Grain, IImageWorker
{
    public Task<byte[]> Resize(byte[] image, int width)
    {
        return Task.FromResult(image);
    }
}
    // </image_worker>

    internal static class ImageWorkerCaller
    {
        internal static async Task Resize(
            IGrainFactory grainFactory,
            byte[] image)
        {
            // <call_image_worker>
IImageWorker worker =
    grainFactory.GetGrain<IImageWorker>("default");

byte[] resized = await worker.Resize(image, width: 320);
            // </call_image_worker>
        }
    }
}

namespace Documentation.Grains.StatelessWorkers.Limited
{
    public interface IImageWorker : IGrainWithStringKey;

    // <limited_image_worker>
[StatelessWorker(maxLocalWorkers: 4)]
public sealed class ImageWorkerGrain : Grain, IImageWorker
{
}
    // </limited_image_worker>
}

namespace Documentation.Grains.Timers
{
    public interface ICacheGrain : IGrainWithStringKey;

    // <grain_timer>
public sealed class CacheGrain : Grain, ICacheGrain
{
    private IGrainTimer? _timer;

    public override Task OnActivateAsync(
        CancellationToken cancellationToken)
    {
        _timer = this.RegisterGrainTimer(
            Refresh,
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.Zero,
                Period = TimeSpan.FromMinutes(1)
            });

        return base.OnActivateAsync(cancellationToken);
    }

    private Task Refresh(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

    // </grain_timer>

    public interface IReportGrain : IGrainWithStringKey;

    // <remindable_report_grain>
public sealed class ReportGrain :
    Grain,
    IReportGrain,
    IRemindable
{
    public Task ReceiveReminder(
        string reminderName,
        TickStatus status)
    {
        return GenerateReport();
    }

    private Task GenerateReport() => Task.CompletedTask;
}
    // </remindable_report_grain>

    internal sealed class ReminderManagementGrain : Grain
    {
        internal async Task Register()
        {
            // <register_reminder>
IGrainReminder reminder = await this.RegisterOrUpdateReminder(
    "daily-report",
    dueTime: TimeSpan.FromMinutes(1),
    period: TimeSpan.FromDays(1));
            // </register_reminder>
        }

        internal async Task Unregister()
        {
            // <unregister_reminder>
IGrainReminder? reminder =
    await this.GetReminder("daily-report");

if (reminder is not null)
{
    await this.UnregisterReminder(reminder);
}
            // </unregister_reminder>
        }
    }

    namespace Documentation.Grains.Reminders
    {
        internal static class ReminderConcurrencyConfiguration
        {
            internal static void Configure(ISiloBuilder siloBuilder)
            {
                // <configure_reminder_concurrency>
                siloBuilder.AddReminderConcurrencyControl(options => options
                    .PerSilo(throttle => throttle
                        .MaxConcurrent(
                            value: 50,
                            blockMode: ThrottleBlockMode.Wait)
                        .PermitsPerSecond(
                            value: 200,
                            burstSize: 200,
                            blockMode: ThrottleBlockMode.Wait)));
                // </configure_reminder_concurrency>
            }
        }
    }
}
