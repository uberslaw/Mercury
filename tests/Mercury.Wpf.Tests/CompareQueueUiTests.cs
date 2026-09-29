namespace Mercury.Wpf.Tests;

public class CompareQueueUiTests
{
    [Fact]
    public void CompareTileIsNotAByteCopy()
    {
        var item = new QueueJobItem(new Job
        {
            Kind = JobKind.Compare,
            Name = "Compare photos",
            Status = JobStatus.Copying,
            SourcePath = @"C:\left",
            DestinationPath = @"C:\right",
            Options = new JobOptions { CompareAdvanced = true, CompareHash = true }
        });
        Assert.Equal("Comparing", item.TileStatus);
        Assert.Equal("Compare. Advanced. Hash.", item.SettingsSummary);
        Assert.Contains("Compare", item.OptionBadges);
        Assert.DoesNotContain(item.OptionBadges, badge => badge.Contains("Unlimited", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Resume this compare from its saved manifest.", new QueueJobItem(new Job
        {
            Kind = JobKind.Compare,
            Status = JobStatus.Cancelled
        }).ResumeToolTip);
    }
}
