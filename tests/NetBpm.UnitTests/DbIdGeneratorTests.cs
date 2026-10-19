using NetBpm.Engine;
using NetBpm.Persistence;

namespace NetBpm.UnitTests;

/// <summary>
/// ID 区块分配的纯单元测试（不碰数据库）。
/// <para>
/// 这组用例守的是一个已实测过的坑：游标初值若取 <c>lastId = 0</c>，首次调用会判定
/// 「区块未用尽」而直接发出 id <c>0</c>，第二次部署就会撞主键
/// （<c>Duplicate entry '0' for key 'PRIMARY'</c>）。
/// </para>
/// </summary>
public sealed class DbIdGeneratorTests
{
    private static readonly string[] FirstBlockOfFour = ["2", "3", "4", "5", "6"];

    [Fact]
    public void NextId_ShouldAcquireBlockOnFirstCall()
    {
        var store = new FakePropertyStore("next.dbid", "1");
        var generator = new DbIdGenerator(store, 2500);

        Assert.Equal("2", generator.NextId());
        Assert.Equal("2501", store.GetValue("next.dbid"));
    }

    [Fact]
    public void NextId_ShouldNeverIssueZero()
    {
        var store = new FakePropertyStore("next.dbid", "1");
        var generator = new DbIdGenerator(store, 2500);

        var ids = Enumerable.Range(0, 10).Select(_ => generator.NextId()).ToList();

        Assert.DoesNotContain("0", ids, StringComparer.Ordinal);
        Assert.Equal(
            Enumerable.Range(2, 10).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ids);
    }

    [Fact]
    public void NextId_ShouldAcquireNextBlockWhenExhausted()
    {
        // 区块大小取 4 便于观察跨越区块的行为
        var store = new FakePropertyStore("next.dbid", "1");
        var generator = new DbIdGenerator(store, 4);

        var ids = Enumerable.Range(0, 5).Select(_ => generator.NextId()).ToList();

        // 第一块 2..5，第二块从 6 起；next.dbid 走 1 → 5 → 9
        Assert.Equal(FirstBlockOfFour, ids);
        Assert.Equal("9", store.GetValue("next.dbid"));
    }

    [Fact]
    public void NextId_ShouldRetryOnOptimisticLockConflict()
    {
        var store = new FakePropertyStore("next.dbid", "1") { ConflictsBeforeSuccess = 2 };
        var generator = new DbIdGenerator(store, 2500);

        Assert.Equal("2", generator.NextId());
        Assert.Equal(3, store.UpdateAttempts);
    }

    [Fact]
    public void NextId_ShouldFailWhenCursorPropertyIsMissing()
    {
        var store = new FakePropertyStore("next.dbid", value: null);
        var generator = new DbIdGenerator(store);

        var exception = Assert.ThrowsAny<EngineException>(() => generator.NextId());
        Assert.Contains("next.dbid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NextId_ShouldFailWhenCursorValueIsNotNumeric()
    {
        var store = new FakePropertyStore("next.dbid", "not-a-number");
        var generator = new DbIdGenerator(store);

        var exception = Assert.ThrowsAny<EngineException>(() => generator.NextId());
        Assert.Contains("not-a-number", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ShouldRejectNonPositiveBlockSize(int blockSize)
    {
        var store = new FakePropertyStore("next.dbid", "1");

        Assert.Throws<ArgumentOutOfRangeException>(() => new DbIdGenerator(store, blockSize));
    }

    /// <summary>内存版属性表，可注入乐观锁冲突。</summary>
    private sealed class FakePropertyStore : IPropertyStore
    {
        private readonly Dictionary<string, PropertyEntity> _properties = new(StringComparer.Ordinal);

        public FakePropertyStore(string name, string? value)
        {
            if (value is not null)
            {
                _properties[name] = new PropertyEntity { Name = name, Value = value, Revision = 1 };
            }
        }

        public int UpdateAttempts { get; private set; }

        public int ConflictsBeforeSuccess { get; init; }

        public PropertyEntity? FindProperty(string name, bool forUpdate = false) =>
            _properties.TryGetValue(name, out var property)
                ? new PropertyEntity { Name = property.Name, Value = property.Value, Revision = property.Revision }
                : null;

        public bool TryUpdateProperty(string name, int expectedRevision, string value)
        {
            UpdateAttempts++;

            if (UpdateAttempts <= ConflictsBeforeSuccess)
            {
                return false;
            }

            if (!_properties.TryGetValue(name, out var property) || property.Revision != expectedRevision)
            {
                return false;
            }

            property.Value = value;
            property.Revision++;
            return true;
        }

        public void InsertProperty(string name, string value, int revision) =>
            _properties[name] = new PropertyEntity { Name = name, Value = value, Revision = revision };

        public string? GetValue(string name) =>
            _properties.TryGetValue(name, out var property) ? property.Value : null;
    }
}
