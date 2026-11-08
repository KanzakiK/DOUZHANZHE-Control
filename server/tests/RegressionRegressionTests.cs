using Douzhanzhe.API;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Douzhanzhe.Tests;

public class RegressionRegressionTests
{
    [Fact]
    public void ProfileService_RefreshFromDisk_PicksUpNewProfiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"douzhan-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var svcA = new ProfileService(dir);
            svcA.EnsureInitialized(dir);
            var svcB = new ProfileService(dir);
            svcB.EnsureInitialized(dir);
            var created = svcB.Create("external");
            Assert.NotNull(created);
            Assert.False(svcA.GetAll().Any(p => p.Id == created!.Id));
            svcA.RefreshFromDisk();
            Assert.True(svcA.GetAll().Any(p => p.Id == created!.Id));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GameProfileService_Update_PreservesStoreMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"douzhan-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new GameProfileService(dir);
            var added = svc.Add(new GameProfile
            {
                Name = "old",
                ExePath = "C:/stub/game.exe",
                ExeName = "game.exe",
                TargetMode = "cfg-gaming",
                Source = "steam",
                StoreAppId = "1234",
                SgdbGameId = "abc",
            });
            svc.Update(added.Id, new GameProfile
            {
                Id = added.Id,
                Name = "new",
                ExePath = added.ExePath,
                ExeName = added.ExeName,
                TargetMode = added.TargetMode,
                Enabled = false,
                Source = added.Source,
                StoreAppId = "9999",
                SgdbGameId = "def",
            });
            var updated = svc.GetAll().Single();
            Assert.Equal("9999", updated.StoreAppId);
            Assert.Equal("def", updated.SgdbGameId);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
