using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Scheduler: the "when Windows starts" trigger. Temp sandboxes and a private registry hive only.</summary>
public class SchedulerTriggerTests
{
    [Fact]
    public void Startup_schedule_uses_a_delayed_logon_trigger_and_reads_old_boot_triggers()
    {
        var s = new CleanSuite.Scheduler.Schedule(CleanSuite.Scheduler.ScheduledTasks.Junk, CleanSuite.Scheduler.ScheduleKind.AtStartup, new DateTime(2026, 10, 21, 9, 0, 0));
        var xml = CleanSuite.Scheduler.TaskXml.Build(s, @"C:\Apps\CleanSuite.App.exe", @"PC\user");
        Assert.DoesNotContain("BootTrigger", xml);
        Assert.Contains("<Delay>" + CleanSuite.Scheduler.TaskXml.StartupDelay + "</Delay>", xml);
        Assert.Equal(CleanSuite.Scheduler.ScheduleKind.AtStartup, CleanSuite.Scheduler.TaskXml.Parse(xml)!.Kind);

        var plainLogon = CleanSuite.Scheduler.TaskXml.Build(s with { Kind = CleanSuite.Scheduler.ScheduleKind.AtLogon }, @"C:\Apps\CleanSuite.App.exe", @"PC\user");
        Assert.DoesNotContain("<Delay>", plainLogon);
        Assert.Equal(CleanSuite.Scheduler.ScheduleKind.AtLogon, CleanSuite.Scheduler.TaskXml.Parse(plainLogon)!.Kind);

        var legacy = xml.Replace("<LogonTrigger>", "<BootTrigger>").Replace("</LogonTrigger>", "</BootTrigger>");
        Assert.Equal(CleanSuite.Scheduler.ScheduleKind.AtStartup, CleanSuite.Scheduler.TaskXml.Parse(legacy)!.Kind);

        var unattended = CleanSuite.Scheduler.ScheduledRunner.UnattendedRegistryCategories;
        foreach (var risky in new[] { CleanSuite.RegistryClean.RegCategory.FileExtensions, CleanSuite.RegistryClean.RegCategory.FileAssociations,
                     CleanSuite.RegistryClean.RegCategory.ActiveXCom, CleanSuite.RegistryClean.RegCategory.SharedDlls,
                     CleanSuite.RegistryClean.RegCategory.ShellExtensions, CleanSuite.RegistryClean.RegCategory.AddRemoveEntries,
                     CleanSuite.RegistryClean.RegCategory.SystemEntries, CleanSuite.RegistryClean.RegCategory.UserEntries })
            Assert.False(unattended.HasFlag(risky), risky.ToString());
        Assert.True(unattended.HasFlag(CleanSuite.RegistryClean.RegCategory.HistoryLists));
    }
}
