using System.IO;

namespace Mercury.Wpf.Tests;

[Collection("WpfSta")]
public class ResumeLastPromptTests
{
    [Fact]
    public void DecliningResumeLast_DoesNotKeepLastJobAsHeaderCurrent()
    {
        WpfSta.Run(() =>
        {
            WpfSta.EnsureApp();
            var root = Path.Combine(Path.GetTempPath(), "mercury-decline-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            MainViewModel? vm = null;
            try
            {
                var paths = new AppPaths(root);
                var src = Path.Combine(root, "src");
                var dest = Path.Combine(root, "dest");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dest);

                var last = new Job
                {
                    Name = "anchor",
                    SourcePath = src,
                    DestinationPath = dest,
                    Status = JobStatus.Incomplete,
                    SourceFiles = 26013,
                    DestFiles = 26013,
                    BytesCopied = 1,
                    ResultMessage = "Incomplete — 12 issue(s). Open the log for details."
                };
                var queued = new Job
                {
                    Name = "next",
                    SourcePath = Path.Combine(root, "other"),
                    DestinationPath = dest,
                    Status = JobStatus.Pending
                };

                using (var journal = JobJournal.Create(paths.JobDirectory(last.Id), last))
                {
                    JobHeartbeat.Write(journal, last.Id, 100, @"Warren Truss\clip.mkv");
                }

                AppSettingsStore.SaveLastJobId(paths, last.Id);
                QueueStore.Save(paths, [last, queued]);

                vm = new MainViewModel(paths);

                Assert.True(vm.ShowProgressDetail, "last-job snapshot is applied before the resume prompt");
                Assert.Equal(100, vm.JobPercent, 2);
                Assert.Equal(@"Warren Truss\clip.mkv", vm.CurrentFile);
                Assert.Equal("26013/26013", vm.JobStats.Files.Value);
                Assert.Equal("1 of 2", vm.JobStats.Job.Value);
                Assert.Contains("Incomplete", vm.ResultBanner, StringComparison.Ordinal);
                Assert.True(vm.ShowOpenLog);
                Assert.Equal("Resume", vm.StartButtonLabel);

                vm.DeclineDirtyResume(last.Id);

                Assert.Equal(QueueStatusHighlight.IdleLabel, vm.HeaderStatusLabel);
                Assert.Equal(QueueStatusHighlight.IdleLabel, vm.HeaderStatusChip.TileStatus);
                Assert.Equal("", vm.CurrentFile);
                Assert.Equal(0, vm.JobPercent);
                Assert.False(vm.ShowProgressDetail);
                Assert.True(string.IsNullOrEmpty(vm.ResultBanner));
                Assert.False(vm.ShowOpenLog);
                Assert.False(vm.JobStats.Files.HasValue);
                Assert.False(vm.JobStats.Job.HasValue);
                Assert.Equal("Start", vm.StartButtonLabel);
                Assert.Equal(2, vm.QueueJobs.Count);
                Assert.True(vm.CanResumeLast);
                Assert.Null(JobHeartbeat.FindDirty(paths));
            }
            finally
            {
                vm?.Dispose();
                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                    // leftover
                }
            }
        });
    }
}
