using Wattkin.Monitoring;
using Xunit;

namespace Wattkin.Tests;

public class PowerModelTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    private static BatteryReading OnBattery(double remainingWh, double watts) =>
        new(BatteryPowerState.Discharging, (uint)(remainingWh * 1000), 15800, (int)(-watts * 1000));

    private static BatteryReading OnCharger(double remainingWh, double chargeWatts) =>
        new(BatteryPowerState.OnLine | (chargeWatts > 0 ? BatteryPowerState.Charging : 0), (uint)(remainingWh * 1000), 16300, (int)(chargeWatts * 1000));

    [Fact]
    public void OnBatteryTheDischargeRateIsTheWholeLaptop()
    {
        var model = new PowerModel();
        var p = model.Update(T0, OnBattery(40, 8), 60_000, 3, 80, dgpuActive: false);
        Assert.Equal(PowerSource.BatteryMeasured, p.Source);
        Assert.Equal(8, p.SystemWatts!.Value, 3);
        Assert.Equal(5, p.TimeToEmpty!.Value.TotalHours, 2);
        Assert.Equal(40 / 60.0 * 100, p.Percent!.Value, 3);
    }

    [Fact]
    public void LearnsRestOfSystemOnBatteryAndUsesItOnTheCharger()
    {
        var model = new PowerModel();
        for (var i = 0; i < 120; i++)
        {
            model.Update(T0.AddSeconds(i), OnBattery(40, 8), 60_000, 3, 80, dgpuActive: false);
        }

        Assert.Equal(5, model.RestOfSystemWatts!.Value, 1);

        var p = model.Update(T0.AddSeconds(200), OnCharger(40, 0), 60_000, 4, 80, dgpuActive: false);
        Assert.Equal(PowerSource.AcEstimated, p.Source);
        Assert.Equal(9, p.SystemWatts!.Value, 1);
        Assert.NotNull(p.UnpluggedRuntimeEstimate);
    }

    /// <summary>A model that has learned 5 W for the rest of the system (8 W on battery with the CPU at 3 W).</summary>
    private static PowerModel Learned()
    {
        var model = new PowerModel();
        for (var i = 0; i < 120; i++)
        {
            model.Update(T0.AddSeconds(i), OnBattery(40, 8), 60_000, 3, 80, dgpuActive: false);
        }

        return model;
    }

    [Fact]
    public void WhileChargingTheChargerGivesTheLaptopsDrawPlusTheBatterysShare()
    {
        var p = Learned().Update(T0.AddSeconds(200), OnCharger(40, 30), 60_000, 4, 80, dgpuActive: false);
        Assert.Equal(9, p.SystemWatts!.Value, 1);
        Assert.Equal(39, p.ChargerWatts!.Value, 1);
    }

    [Fact]
    public void AtTheLimitTheChargerOnlyRunsTheLaptop()
    {
        var p = Learned().Update(T0.AddSeconds(200), OnCharger(48, 0), 60_000, 4, 80, dgpuActive: false);
        Assert.Equal(9, p.ChargerWatts!.Value, 1);
    }

    [Fact]
    public void RunningFromTheBatteryWhilePluggedInTheChargerGivesNothing()
    {
        var drain = new BatteryReading(BatteryPowerState.OnLine | BatteryPowerState.Discharging, 50_000, 16000, -10_000);
        var p = Learned().Update(T0.AddSeconds(200), drain, 60_000, 4, 80, dgpuActive: false);
        Assert.Equal(0, p.ChargerWatts!.Value, 1);
    }

    [Fact]
    public void NoChargerNumberFromTheCpuAloneOrOnBattery()
    {
        Assert.Null(new PowerModel().Update(T0, OnCharger(40, 30), 60_000, 4, 80, dgpuActive: false).ChargerWatts);
        Assert.Null(Learned().Update(T0.AddSeconds(200), OnBattery(40, 8), 60_000, 3, 80, dgpuActive: false).ChargerWatts);
    }

    [Fact]
    public void AWorkingRtxAddsItsOwnReadingToTheChargerNumberOnly()
    {
        // Only read while a window is open: in the laptop's estimate (tray, averages) it would come and go.
        var p = Learned().Update(T0.AddSeconds(200), OnCharger(40, 30), 60_000, 4, 80, dgpuActive: true, dgpuWatts: 60);
        Assert.Equal(9, p.SystemWatts!.Value, 1);
        Assert.Equal(99, p.ChargerWatts!.Value, 1);
    }

    [Fact]
    public void DoesNotLearnWhileTheDgpuIsActive()
    {
        var model = new PowerModel();
        for (var i = 0; i < 120; i++)
        {
            model.Update(T0.AddSeconds(i), OnBattery(40, 30), 60_000, 3, 80, dgpuActive: true);
        }

        Assert.Null(model.RestOfSystemWatts);
    }

    [Fact]
    public void OnChargerWithoutLearningReportsCpuOnly()
    {
        var model = new PowerModel();
        var p = model.Update(T0, OnCharger(40, 0), 60_000, 4.5, 80, dgpuActive: false);
        Assert.Equal(PowerSource.AcCpuOnly, p.Source);
        Assert.Equal(4.5, p.SystemWatts!.Value, 3);
        Assert.Null(p.UnpluggedRuntimeEstimate);
    }

    [Fact]
    public void TimeToChargeLimitUsesTheTargetNotFull()
    {
        var model = new PowerModel();
        var p = model.Update(T0, OnCharger(40, 20), 60_000, 4, 80, dgpuActive: false);
        // target 48 Wh, 8 Wh to go at 20 W = 0.4 h
        Assert.Equal(0.4, p.TimeToTarget!.Value.TotalHours, 3);
    }

    [Fact]
    public void SessionAverageComesFromEnergyUsedSinceUnplugging()
    {
        var model = new PowerModel();
        model.Update(T0, OnCharger(50, 0), 60_000, 4, 80, dgpuActive: false);
        model.Update(T0.AddSeconds(1), OnBattery(50, 8), 60_000, 3, 80, dgpuActive: false);
        var p = model.Update(T0.AddSeconds(1).AddHours(1), OnBattery(42, 8), 60_000, 3, 80, dgpuActive: false);
        Assert.NotNull(p.Session);
        Assert.Equal(8, p.Session!.AverageWatts!.Value, 2);
        Assert.Equal(42 / 8.0, p.Session.ProjectedRemaining!.Value.TotalHours, 2);
    }

    [Fact]
    public void UnknownBatteryRateIsNotTreatedAsZeroDraw()
    {
        var model = new PowerModel();
        var p = model.Update(T0, new BatteryReading(BatteryPowerState.Discharging, 40_000, 15800, null), 60_000, 3, 80, dgpuActive: false);
        Assert.NotEqual(PowerSource.BatteryMeasured, p.Source);
        Assert.Null(p.TimeToEmpty);
    }

    [Fact]
    public void ImpossibleGaugeSpikeAtUnplugIsIgnored()
    {
        // Seen on the real laptop: the first reading after unplugging was ~1,063 W.
        var model = new PowerModel();
        var spike = model.Update(T0, OnBattery(57, 1063), 60_000, 5, 80, dgpuActive: false);
        Assert.NotEqual(PowerSource.BatteryMeasured, spike.Source);
        Assert.Null(spike.WattsNow);

        var next = model.Update(T0.AddSeconds(5), OnBattery(57, 17), 60_000, 5, 80, dgpuActive: false);
        Assert.Equal(17, next.WattsNow!.Value, 1);
    }

    [Fact]
    public void TheWattsShownAreTheLatestReadingNotAnAverage()
    {
        var model = new PowerModel();
        model.Update(T0, OnBattery(40, 8), 60_000, 3, 80, dgpuActive: false);
        var p = model.Update(T0.AddSeconds(2), OnBattery(40, 25), 60_000, 3, 80, dgpuActive: false);
        Assert.Equal(25, p.WattsNow!.Value, 3);
    }

    /// <summary>Ten minutes at 8 W, then the given draw for the given time, sampled every 5 s.</summary>
    private static PowerState AfterSteady8W(double watts, int seconds)
    {
        var model = new PowerModel();
        PowerState p = null!;
        var t = T0;
        for (var i = 0; i < 120; i++, t = t.AddSeconds(5))
        {
            p = model.Update(t, OnBattery(40, 8), 60_000, 3, 80, dgpuActive: false);
        }

        for (var i = 0; i < seconds / 5; i++, t = t.AddSeconds(5))
        {
            p = model.Update(t, OnBattery(40, watts), 60_000, 3, 80, dgpuActive: false);
        }

        return p;
    }

    [Fact]
    public void TimeLeftShrugsOffAShortBurst()
    {
        // 40 s of heavy work after ten quiet minutes: the estimate moves a little, not by hours.
        var p = AfterSteady8W(40, 40);
        Assert.InRange(p.AverageWatts!.Value, 8, 11.5);
        Assert.InRange(p.TimeToEmpty!.Value.TotalHours, 3.4, 5);
    }

    [Fact]
    public void TimeLeftFollowsASustainedChange()
    {
        // Three minutes at 30 W is a new workload: the estimate follows it.
        var p = AfterSteady8W(30, 180);
        Assert.InRange(p.AverageWatts!.Value, 26, 30.5);
    }

    [Fact]
    public void FailedBatteryReadKeepsTheLastChargerState()
    {
        var model = new PowerModel();
        model.Update(T0, OnBattery(40, 8), 60_000, 3, 80, dgpuActive: false);
        var p = model.Update(T0.AddSeconds(5), null, 60_000, 3, 80, dgpuActive: false);
        Assert.False(p.OnAc);
    }
}
