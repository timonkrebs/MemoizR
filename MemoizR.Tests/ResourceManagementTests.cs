using MemoizR.StructuredConcurrency;

namespace MemoizR.Tests;

public class ResourceManagementTests
{
    private class TestResource : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private class AsyncTestResource : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task TestResourceDisposal()
    {
        var f = new MemoFactory();
        var resource1 = new TestResource();
        var resource2 = new AsyncTestResource();

        var c1 = f.CreateConcurrentMapReduce(
            async r =>
            {
                r.AddResource(resource1);
                r.AddResource(resource2);
                await Task.Delay(10);
                return 1;
            },
            async r =>
            {
                await Task.Delay(10);
                return 2;
            });

        Assert.Equal(3, await c1.Get());
        Assert.True(resource1.IsDisposed);
        Assert.True(resource2.IsDisposed);
    }

    [Fact]
    public async Task TestResourceDisposalOnFailure()
    {
        var f = new MemoFactory();
        var resource = new TestResource();

        var c1 = f.CreateConcurrentMapReduce(
            async r =>
            {
                r.AddResource(resource);
                await Task.Delay(10);
                throw new Exception("Test failure");
            },
            async r =>
            {
                await Task.Delay(100);
                return 2;
            });

        await Assert.ThrowsAsync<AggregateException>(async () => await c1.Get());
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public async Task TestResourceDisposalOrder()
    {
        var f = new MemoFactory();
        var disposalOrder = new List<int>();

        var resource1 = new ActionDisposable(() => disposalOrder.Add(1));
        var resource2 = new ActionDisposable(() => disposalOrder.Add(2));

        var c1 = f.CreateConcurrentMapReduce(
            r =>
            {
                r.AddResource(resource1);
                r.AddResource(resource2);
                return Task.FromResult(1);
            });

        await c1.Get();
        Assert.Equal(new[] { 2, 1 }, disposalOrder);
    }

    [Fact]
    public async Task TestRaceResourceDisposal()
    {
        var f = new MemoFactory();
        var resource1 = new TestResource();
        var resource2 = new TestResource();

        var c1 = f.CreateConcurrentRace(
            () => Task.FromResult("input"),
            async (r, i) =>
            {
                r.AddResource(resource1);
                await Task.Delay(10);
                return 1;
            },
            async (r, i) =>
            {
                r.AddResource(resource2);
                await Task.Delay(1000);
                return 2;
            });

        await c1.Get();
        Assert.True(resource1.IsDisposed);
        Assert.True(resource2.IsDisposed);
    }


    [Fact]
    public async Task TestResourceDisposalExceptionsAreAggregated()
    {
        var f = new MemoFactory();

        var resource1 = new ActionDisposable(() => throw new InvalidOperationException("dispose 1"));
        var resource2 = new ActionDisposable(() => throw new InvalidOperationException("dispose 2"));

        var c1 = f.CreateConcurrentMapReduce(
            r =>
            {
                r.AddResource(resource1);
                r.AddResource(resource2);
                return Task.FromResult(1);
            });

        var ex = await Assert.ThrowsAsync<AggregateException>(async () => await c1.Get());
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Equal("dispose 2", ex.InnerExceptions[0].Message);
        Assert.Equal("dispose 1", ex.InnerExceptions[1].Message);
    }

    private class ActionDisposable : IDisposable
    {
        private readonly Action action;
        public ActionDisposable(Action action) => this.action = action;
        public void Dispose() => action();
    }
}
