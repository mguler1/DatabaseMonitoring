using DatabaseMonitoring.Web.Models;
using DatabaseMonitoring.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace DatabaseMonitoring.Tests;

/// <summary>Testler için yalnızca ContentRootPath'in anlamlı olduğu minimal sahte ortam.</summary>
file class FakeWebHostEnvironment : IWebHostEnvironment
{
    public string ContentRootPath { get; set; } = "";
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "DatabaseMonitoring.Tests";
    public string WebRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public class ConnectionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ConnectionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dbmon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private ConnectionStore CreateStore() => new(new FakeWebHostEnvironment { ContentRootPath = _tempDir });

    [Fact]
    public void GetAll_NoFileYet_ReturnsEmpty()
    {
        var store = CreateStore();
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Add_PersistsAndAssignsIdAndTimestamp()
    {
        var store = CreateStore();
        store.Add(new ConnectionEntry { Name = "Test DB", Type = "mssql", ConnectionString = "Server=.;" });

        var all = store.GetAll();
        Assert.Single(all);
        Assert.False(string.IsNullOrWhiteSpace(all[0].Id));
        Assert.Equal("Test DB", all[0].Name);
        Assert.True(all[0].AddedAt > DateTime.Now.AddMinutes(-1));
    }

    [Fact]
    public void Add_ThenReloadingNewStoreInstance_SeesSameData()
    {
        var store = CreateStore();
        store.Add(new ConnectionEntry { Name = "Persisted", Type = "postgresql", ConnectionString = "Host=.;" });

        var reloaded = CreateStore();
        Assert.Single(reloaded.GetAll());
        Assert.Equal("Persisted", reloaded.GetAll()[0].Name);
    }

    [Fact]
    public void GetActive_OnlyReturnsActiveEntries()
    {
        var store = CreateStore();
        store.Add(new ConnectionEntry { Name = "Active", Type = "mssql", ConnectionString = "x", IsActive = true });
        store.Add(new ConnectionEntry { Name = "Inactive", Type = "mssql", ConnectionString = "x", IsActive = false });

        var active = store.GetActive();
        Assert.Single(active);
        Assert.Equal("Active", active[0].Name);
    }

    [Fact]
    public void ToggleActive_FlipsIsActive()
    {
        var store = CreateStore();
        store.Add(new ConnectionEntry { Name = "Toggle Me", Type = "mssql", ConnectionString = "x" });
        var id = store.GetAll()[0].Id;

        store.ToggleActive(id);
        Assert.False(store.GetById(id)!.IsActive);

        store.ToggleActive(id);
        Assert.True(store.GetById(id)!.IsActive);
    }

    [Fact]
    public void Update_ExistingEntry_ChangesFields()
    {
        var store = CreateStore();
        store.Add(new ConnectionEntry { Name = "Old", Type = "mssql", ConnectionString = "old" });
        var id = store.GetAll()[0].Id;

        var updated = store.Update(id, "New", "postgresql", "new-conn-string");

        Assert.True(updated);
        var entry = store.GetById(id)!;
        Assert.Equal("New", entry.Name);
        Assert.Equal("postgresql", entry.Type);
        Assert.Equal("new-conn-string", entry.ConnectionString);
    }

    [Fact]
    public void Update_UnknownId_ReturnsFalse()
    {
        var store = CreateStore();
        Assert.False(store.Update("missing-id", "x", "mssql", "y"));
    }

    [Fact]
    public void Delete_RemovesEntry()
    {
        var store = CreateStore();
        store.Add(new ConnectionEntry { Name = "ToDelete", Type = "mssql", ConnectionString = "x" });
        var id = store.GetAll()[0].Id;

        store.Delete(id);

        Assert.Empty(store.GetAll());
        Assert.Null(store.GetById(id));
    }
}
