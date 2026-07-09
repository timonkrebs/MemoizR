using Xunit;

namespace MemoizR.Tests.StructuredConcurrency
{
    public class ConcurrentMapTests
    {
        // The shared value must not be castable back to a writable array: one reader mutating
        // it would race every other flow, bypassing the Sendable rules that reject arrays as
        // writable shared state. The map publishes an O(1) read-only wrapper instead.
        [Fact(Timeout = 10000)]
        public async Task ConcurrentMap_PublishesAReadOnlyValue_NotTheBackingArray()
        {
            var f = new MemoFactory();
            var map = f.CreateConcurrentMap(
                async (cts) => 1,
                async (cts) => 2);

            var value = await map.Get();

            Assert.Equal([1, 2], value);
            Assert.IsNotType<int[]>(value, exactMatch: false);
        }

        [Fact(Timeout = 1000)]
        public async Task TestConcurrentMap()
        {
            var f = new MemoFactory();

            var v1 = f.CreateSignal(1);
            var v2 = f.CreateSignal(2);
            var v3 = f.CreateSignal(3);

            var map = f.CreateConcurrentMap(
                async (cts) => await v1.Get(),
                async (cts) => await v2.Get(),
                async (cts) => await v3.Get());

            Assert.Equal([1, 2, 3], await map.Get());

            await v1.Set(4);
            Assert.Equal([4, 2, 3], await map.Get());

            await v2.Set(5);
            Assert.Equal([4, 5, 3], await map.Get());

            await v3.Set(6);
            Assert.Equal([4, 5, 6], await map.Get());
        }

        [Fact(Timeout = 1000)]
        public async Task TestConcurrentMapWithDiamondDependencies()
        {
            // Memos composed over a ConcurrentMap are typed IEnumerable<T> -- an interface, which
            // the (now default) strict Sendable checks reject by principle. Known migration
            // friction: compose over an immutable type, or opt out per factory as here.
            var f = new MemoFactory(options: MemoFactoryOptions.DisableSendableChecks);

            var v1 = f.CreateSignal(1);
            var v2 = f.CreateSignal(2);
            var v3 = f.CreateSignal(3);

            var map1 = f.CreateConcurrentMap(
                async (cts) => await v1.Get(),
                async (cts) => await v2.Get(),
                async (cts) => await v3.Get());

            var map2 = f.CreateConcurrentMap(
                async (cts) => await map1.Get(),
                async (cts) => await map1.Get());

            var map3 = f.CreateConcurrentMap(
                async (cts) => await map2.Get(),
                async (cts) => await map2.Get());

            Assert.Equal([1, 2, 3], await map1.Get());
            Assert.Equal([[1, 2, 3], [1, 2, 3]], await map2.Get());
            Assert.Equal([[[1, 2, 3], [1, 2, 3]], [[1, 2, 3], [1, 2, 3]]], await map3.Get());

            await v1.Set(4);
            Assert.Equal([4, 2, 3], await map1.Get());
            Assert.Equal([[4, 2, 3], [4, 2, 3]], await map2.Get());
            Assert.Equal([[[4, 2, 3], [4, 2, 3]], [[4, 2, 3], [4, 2, 3]]], await map3.Get());

            await v2.Set(5);
            Assert.Equal([4, 5, 3], await map1.Get());
            Assert.Equal([[4, 5, 3], [4, 5, 3]], await map2.Get());
            Assert.Equal([[[4, 5, 3], [4, 5, 3]], [[4, 5, 3], [4, 5, 3]]], await map3.Get());

            await v3.Set(6);
            Assert.Equal([4, 5, 6], await map1.Get());
            Assert.Equal([[4, 5, 6], [4, 5, 6]], await map2.Get());
            Assert.Equal([[[4, 5, 6], [4, 5, 6]], [[4, 5, 6], [4, 5, 6]]], await map3.Get());
        }
    }
}