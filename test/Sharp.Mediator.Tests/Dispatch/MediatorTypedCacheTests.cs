using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator.Bindings;
using Sharp.Mediator.Fixtures;
using Sharp.Mediator.Publishers;

namespace Sharp.Mediator.Tests.Dispatch;

// Pins the mediator's statically-typed cache (per-instance slot arrays): cold slow-path slot writes, warm Unsafe.As
// slot hits, the publisher array loops, and the object/interface-typed paths that go through the route tables.
public sealed class MediatorTypedCacheTests
{
    private static IServiceProvider Provider(Action<MediatorBuilder> configure, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddSingleton<TraceLog>();
        extra?.Invoke(services);
        services.AddMediator(configure);
        return services.BuildServiceProvider();
    }

    // ---- typed request: cold slow-path write -> warm slot hit -------------------------------------------------------

    [Fact]
    public async Task VoidRequest_CachedMediatorColdThenWarm_HitsSlotArray()
    {
        var sp = Provider(x => x.AddHandler<VoidRequestHandler>());
        var mediator = new DefaultMediator(sp, 1);
        var counter = sp.GetRequiredService<Counter>();

        await mediator.Request(new VoidRequest()); // cold: RequestVoidSlow resolves + writes the slot
        await mediator.Request(new VoidRequest()); // warm: Unsafe.As cached delegate

        Assert.Equal(2, counter.Value);
    }

    [Fact]
    public async Task ResponseRequest_CachedMediatorColdThenWarm_HitsSlotArray()
    {
        var sp = Provider(x => x.AddHandler<EchoRequestHandler>());
        var mediator = new DefaultMediator(sp, 1);
        Assert.Equal("echo:a", await mediator.Request<EchoRequest, string>(new EchoRequest("a"))); // cold
        Assert.Equal("echo:b", await mediator.Request<EchoRequest, string>(new EchoRequest("b"))); // warm
    }

    [Fact]
    public async Task RequestStream_CachedMediatorColdThenWarm_HitsSlotArray()
    {
        var sp = Provider(x => x.AddHandler<StreamRequestHandler>());
        var mediator = new DefaultMediator(sp, 1);
        Assert.Equal([0, 1], await Collect(mediator.RequestStream<StreamRequest, int>(new StreamRequest(2)))); // cold
        Assert.Equal([0, 1], await Collect(mediator.RequestStream<StreamRequest, int>(new StreamRequest(2)))); // warm
    }

    // ---- publish: cold -> warm + inlined PublishParallel arms -------------------------------------------------------

    [Fact]
    public async Task Publish_CachedMediatorParallelDefaultColdThenWarm_AllSyncHandlers()
    {
        var sp = Provider(x => x.AddHandler<FirstEventHandler>().AddHandler<SecondEventHandler>());
        var mediator = new DefaultMediator(sp, 1);
        var log = sp.GetRequiredService<TraceLog>();

        await mediator.Publish(new TestEvent("a")); // cold: PublishSlow writes slot, ParallelPublishing (count 2, all sync)
        await mediator.Publish(new TestEvent("b")); // warm: cached entry -> ParallelPublishing

        Assert.Equal(4, log.Entries.Count);
    }

    [Fact]
    public async Task Publish_CachedMediatorSequentialDefaultColdThenWarm_UsesPublisherPublish()
    {
        var sp = Provider(x => x
            .WithEventPublisher<SequentialPublisher>()
            .AddHandler<FirstEventHandler>()
            .AddHandler<SecondEventHandler>());

        var mediator = new DefaultMediator(sp, 1);
        var log = sp.GetRequiredService<TraceLog>();

        await mediator.Publish(new TestEvent("a")); // cold
        await mediator.Publish(new TestEvent("b")); // warm: entry.Publisher is not ParallelPublisher -> publisher.Publish

        Assert.Equal(["first:a", "second:a", "first:b", "second:b"], log.Entries);
    }

    [Fact]
    public async Task Publish_CachedMediatorParallelDefaultAsyncHandler_TakesWhenAllPath()
    {
        // Async handler is registered first -> chains[0] suspends, so ParallelPublishing allocates the pending Task[]
        // and the inner AsTask loop runs over the trailing sync chain (the Task.WhenAll branch).
        var sp = Provider(x => x
            .AddHandler<ParallelAsyncEventHandler>()
            .AddHandler<ParallelSyncEventHandler>());

        var mediator = new DefaultMediator(sp, 1);
        var log = sp.GetRequiredService<TraceLog>();

        await mediator.Publish(new ParallelEvent("x")); // cold
        await mediator.Publish(new ParallelEvent("y")); // warm

        Assert.Equal(4, log.Entries.Count);
        Assert.Contains("async:x", log.Entries);
        Assert.Contains("sync:y", log.Entries);
    }

    // ---- publish with explicit publisher ----------------------------------------------------------------------------

    [Fact]
    public async Task PublishWithOverride_CachedMediatorAfterWarming_UsesCachedEntry()
    {
        var sp = Provider(x => x.AddHandler<FirstEventHandler>().AddHandler<SecondEventHandler>());
        var mediator = new DefaultMediator(sp, 1);
        var log = sp.GetRequiredService<TraceLog>();

        await mediator.Publish(new TestEvent("warm")); // warm the slot first
        await mediator.Publish(new TestEvent("over"), new SequentialPublisher()); // warm + override -> Unsafe.As entry, publisher.Publish

        Assert.Equal(["first:over", "second:over"], log.Entries.Skip(2).ToArray());
    }

    [Fact]
    public async Task PublishWithOverride_CachedMediatorColdPath_WritesCacheEntry()
    {
        var sp = Provider(x => x.AddHandler<FirstEventHandler>());
        var mediator = new DefaultMediator(sp, 1);
        var publisher = new SequentialPublisher();

        await mediator.Publish(new TestEvent("cold"), publisher);
        await mediator.Publish(new TestEvent("warm"), publisher);

        Assert.Equal(["first:cold", "first:warm"], sp.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    // A caching mediator that meets an event another instance has recorded as complete resolves its handlers
    // straight from the entry point, past an empty slot of its array, and still uses the given publisher. The array
    // holds the slots up to the highest the mediator caches, so a request first dispatched after the event sizes it
    // past the event's.
    public async Task PublishWithOverride_CachedMediatorFirstPublishOfACompleteEvent_UsesTheGivenPublisher()
    {
        var sp = Provider(x => x.AddHandler<FirstEventHandler>().AddHandler<SizingRequestHandler>());
        await new DefaultMediator(sp).Publish(new TestEvent("record"));
        var mediator = new DefaultMediator(sp, 1);
        await mediator.Request<SizingRequest, int>(new SizingRequest());

        var publisher = new LevelCountingPublisher();

        Assert.True(MessageRoutes.IsComplete<TestEvent>());
        Assert.Null(Chains(mediator)[EventSlot<TestEvent>.Index]);

        await mediator.Publish(new TestEvent("given"), publisher);

        Assert.Equal(["TestEvent"], publisher.Calls);
        Assert.Equal(["first:record", "first:given"], sp.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    public async Task PublishWithNullOverride_PlainMediator_DelegatesToDefault()
    {
        var sp = Provider(x => x.AddHandler<FirstEventHandler>());
        var mediator = new DefaultMediator(sp);
        var log = sp.GetRequiredService<TraceLog>();
        await mediator.Publish(new TestEvent("n"), null); // publisher is null -> Publish(message, ct)

        Assert.Contains("first:n", log.Entries);
    }

    // ---- interface-typed + object dispatch through the resolved dispatcher ------------------------------------------

    [Fact]
    public async Task InterfaceTypedRequest_PlainMediator_UsesTheRouteTable()
    {
        var sp = Provider(x => x.AddHandler<EchoRequestHandler>());
        var mediator = new DefaultMediator(sp);
        IRequest<string> request = new EchoRequest("i");
        Assert.Equal("echo:i", await mediator.Request(request));
    }

    [Fact]
    public async Task InterfaceTypedRequestStream_PlainMediator_UsesTheRouteTable()
    {
        var sp = Provider(x => x.AddHandler<StreamRequestHandler>());
        var mediator = new DefaultMediator(sp);
        IStreamRequest<int> request = new StreamRequest(3);
        Assert.Equal([0, 1, 2], await Collect(mediator.RequestStream(request)));
    }

    [Fact]
    public async Task ObjectRequest_PlainMediator_RepeatsThroughTheRouteTable()
    {
        var sp = Provider(x => x.AddHandler<EchoRequestHandler>());
        var mediator = new DefaultMediator(sp);
        Assert.Equal("echo:1", await mediator.Request((object)new EchoRequest("1")));
        Assert.Equal("echo:2", await mediator.Request((object)new EchoRequest("2")));
    }

    [Fact]
    public async Task ObjectPublish_PlainMediator_UsesTheRouteTable()
    {
        var sp = Provider(x => x.AddHandler<FirstEventHandler>());
        var mediator = new DefaultMediator(sp);
        var log = sp.GetRequiredService<TraceLog>();
        await mediator.Publish((object)new TestEvent("object"));

        Assert.Contains("first:object", log.Entries);
    }

    [Fact]
    public async Task ObjectStream_PlainMediator_UsesTheRouteTable()
    {
        var sp = Provider(x => x.AddHandler<StreamRequestHandler>());
        var mediator = new DefaultMediator(sp);
        var items = await Collect(mediator.RequestStream((object)new StreamRequest(2)));
        Assert.Equal([0, 1], items);
    }

    // ---- cache warm-up ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cache_IsNotAllocatedUntilEnoughMisses_ThenStopsResolving()
    {
        // A request scope usually dispatches once or twice, so an array sized by every message would never pay off.
        var counting = new CountingServiceProvider(
            Provider(x => x.AddHandler<EchoRequestHandler>()), typeof(IRequestHandler<EchoRequest, string>));

        var mediator = new DefaultMediator(counting, 3);
        for (var i = 0; i < 5; i++)
            await mediator.Request<EchoRequest, string>(new EchoRequest("x"));

        Assert.Equal(3, counting.Resolutions);
    }

    [Fact]
    public async Task UncachedMediator_ResolvesOnEveryCall()
    {
        var counting = new CountingServiceProvider(
            Provider(x => x.AddHandler<EchoRequestHandler>()), typeof(IRequestHandler<EchoRequest, string>));

        var mediator = new DefaultMediator(counting);
        await mediator.Request<EchoRequest, string>(new EchoRequest("x"));
        await mediator.Request<EchoRequest, string>(new EchoRequest("x"));

        Assert.Equal(2, counting.Resolutions);
    }

    // ---- null messages ----------------------------------------------------------------------------------------------

    [SuppressMessage("Reliability", "CA2012",
        Justification = "The test asserts a synchronous throw, or the benchmark uses handlers known to complete synchronously.")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullMessage_ThrowsOnEveryEntryPoint_ColdOrWarm(bool warm)
    {
        var sp = Provider(x => x
            .AddHandler<VoidRequestHandler>()
            .AddHandler<EchoRequestHandler>()
            .AddHandler<FirstEventHandler>()
            .AddHandler<StreamRequestHandler>());

        var mediator = new DefaultMediator(sp, 1);
        if (warm)
        {
            await mediator.Request(new VoidRequest());
            await mediator.Request<EchoRequest, string>(new EchoRequest("x"));
            await mediator.Publish(new TestEvent("x"));
            await Collect(mediator.RequestStream<StreamRequest, int>(new StreamRequest(1)));
        }

        VoidRequest voidRequest = null!;
        EchoRequest echoRequest = null!;
        TestEvent testEvent = null!;
        StreamRequest streamRequest = null!;
        object untyped = null!;

        AssertNullMessage(() => mediator.Request(voidRequest));
        AssertNullMessage(() => mediator.Request<EchoRequest, string>(echoRequest));
        AssertNullMessage(() => mediator.Request(echoRequest));
        AssertNullMessage(() => mediator.Request(untyped));
        AssertNullMessage(() => mediator.Publish(testEvent));
        AssertNullMessage(() => mediator.Publish(testEvent, null));
        AssertNullMessage(() => mediator.Publish(testEvent, new SequentialPublisher()));
        AssertNullMessage(() => mediator.Publish(untyped));
        AssertNullMessage(() => mediator.RequestStream<StreamRequest, int>(streamRequest));
        AssertNullMessage(() => mediator.RequestStream(streamRequest));
        AssertNullMessage(() => mediator.RequestStream(untyped));
    }

    // ---- a message touched after the cache was sized ----------------------------------------------------------------

    [Fact]
    public async Task Cache_Grows_ForAMessageTouchedAfterItWasSized()
    {
        // Slots are taken on first use and only this test touches LateRequest, so its slot does not exist until the
        // second dispatch and lands past the end of the array the first dispatch sized.
        var provider = new CountingServiceProvider(
            Provider(x => x.AddHandler<EchoRequestHandler>().AddHandler<LateRequestHandler>()),
            typeof(IRequestHandler<LateRequest, int>));

        var mediator = new DefaultMediator(provider, 1);
        await mediator.Request<EchoRequest, string>(new EchoRequest("sized"));
        Assert.Equal(1, await mediator.Request<LateRequest, int>(new LateRequest())); // grows the array
        Assert.Equal(1, await mediator.Request<LateRequest, int>(new LateRequest())); // warm
        Assert.Equal(1, provider.Resolutions);
    }

    [Fact]
    // A long-lived mediator meets each message past the end of its array, since slots are taken on first use. Growing
    // to exactly the slot count copied the whole array for every new message.
    public async Task Cache_AtLeastDoubles_WhenItGrows()
    {
        var mediator = new DefaultMediator(
            Provider(x => x.AddHandler<EchoRequestHandler>().AddHandler<LaterRequestHandler>()), 1);

        await mediator.Request<EchoRequest, string>(new EchoRequest("sized"));
        var sized = Chains(mediator).Length;

        Assert.Equal(1, await mediator.Request<LaterRequest, int>(new LaterRequest())); // its slot lands past the end
        Assert.True(Chains(mediator).Length >= 2 * sized, $"grew from {sized} to {Chains(mediator).Length}");
    }

    [Fact]
    // The array of a mediator that caches from its first dispatch holds the slots up to the highest it caches. It used
    // to hold one for every message the process had touched.
    public async Task Cache_IsSizedByTheSlotsTheMediatorUses()
    {
        var index = RequestSlot<EchoRequest, string>.Index;
        for (var i = 0; i < 1000; i++) MediatorRuntime.NextSlot(); // messages the rest of the process has touched

        var mediator = new DefaultMediator(Provider(x => x.AddHandler<EchoRequestHandler>()), 1);
        await mediator.Request<EchoRequest, string>(new EchoRequest("sized"));
        Assert.InRange(Chains(mediator).Length, index + 1, 2 * (index + 1));
    }

    // ---- the chain of a high slot -----------------------------------------------------------------------------------
    //
    // Slots go to messages in the order the process first touches them. A mediator that waits for misses before it
    // caches, as a Scoped one does, keeps the chains of slots from DenseSlots up beside an array that holds the lower
    // ones, in its SparseChains, until an array of their slots takes no more memory than the table. These mediators
    // cache from their second miss.

    [Fact]
    // A busy request scope of a message the process first dispatched after a thousand others used to allocate an array
    // of every slot up to the message's, 8 KB, to keep one chain. Its mediator keeps the chain beside an array of one
    // element, which holds nothing but the table, and finds it there.
    public async Task Cache_KeepsTheChainOfAHighSlotBesideItsArray()
    {
        while (MediatorRuntime.NextSlot() < 1000)
        {
        } // messages the rest of the process has dispatched

        var provider = new CountingServiceProvider(
            Provider(x => x.AddHandler<HighSlotRequestHandler>()),
            typeof(IRequestHandler<HighSlotRequest, int>));

        var mediator = new DefaultMediator(provider, 2);
        Assert.Equal(1, await HighSlot.Request(mediator));
        Assert.Equal(1, await HighSlot.Request(mediator)); // caches
        Assert.Equal(1, await HighSlot.Request(mediator)); // finds it beside the array
        Assert.True(HighSlot.RequestIndex > 1000);
        Assert.IsType<SparseChains>(Assert.Single(Chains(mediator)));
        Assert.Equal(2, provider.Resolutions);
    }

    [Fact]
    // Every kind of chain is found beside the array before its handlers are resolved again.
    public async Task VoidRequestAndStreamAndEvent_OfHighSlots_AreFoundBesideTheArray()
    {
        while (MediatorRuntime.NextSlot() < DefaultMediator.DenseSlots)
        {
        }

        var resolutions = new CountingServiceProvider(
            Provider(x => x.AddHandler<HighSlotCommandHandler>().AddHandler<HighSlotStreamHandler>()
                .AddHandler<HighSlotNoticeHandler>()),
            typeof(IRequestHandler<HighSlotCommand>), typeof(IStreamRequestHandler<HighSlotStream, int>),
            typeof(IEnumerable<IEventHandler<HighSlotNotice>>));

        var mediator = new DefaultMediator(resolutions, 2);
        for (var round = 0; round < 3; round++)
        {
            if (round == 2) Assert.IsType<SparseChains>(Assert.Single(Chains(mediator)));

            var resolved = resolutions.Resolutions;

            await HighSlot.Command(mediator);
            Assert.Equal([0, 1], await Collect(HighSlot.Stream(mediator)));

            await HighSlot.Notice(mediator);

            if (round == 2) Assert.Equal(resolved, resolutions.Resolutions);
        }

        Assert.Equal(3, resolutions.GetRequiredService<Counter>().Value);
        Assert.Equal(["notice", "notice", "notice"], resolutions.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    // A publish that names its publisher takes an entry kept beside the array as it takes one from the array, and
    // publishes through the publisher it names.
    public async Task PublishWithOverride_TakesTheEntryOfAHighSlotFromBesideTheArray()
    {
        while (MediatorRuntime.NextSlot() < DefaultMediator.DenseSlots)
        {
        }

        var resolutions = new CountingServiceProvider(
            Provider(x => x.AddHandler<HighSlotNoticeHandler>()),
            typeof(IEnumerable<IEventHandler<HighSlotNotice>>));

        var mediator = new DefaultMediator(resolutions, 2);
        await HighSlot.Notice(mediator);
        await HighSlot.Notice(mediator); // caches the entry beside the array
        var publisher = new LevelCountingPublisher();
        await HighSlot.Notice(mediator, publisher);

        Assert.IsType<SparseChains>(Assert.Single(Chains(mediator)));
        Assert.Equal(2, resolutions.Resolutions);
        Assert.Equal(["HighSlotNotice"], publisher.Calls);
        Assert.Equal(["notice", "notice", "notice"], resolutions.GetRequiredService<TraceLog>().Entries);
    }

    [Fact]
    // A publisher that receives every level gets each level's executors and those of all levels together from beside
    // the array: those of a level that another publish cached, and the same array of all levels on each publish.
    public async Task CustomPublisher_TakesTheExecutorsOfHighSlotsFromBesideTheArray()
    {
        while (MediatorRuntime.NextSlot() < DefaultMediator.DenseSlots)
        {
        }

        var publisher = new LevelCountingPublisher();
        var resolutions = new CountingServiceProvider(
            Provider(x => x.AddHandler<HighSlotLevelHandler>().AddHandler<HighSlotLevelBaseHandler>(),
                services => services.AddSingleton<IEventPublisher>(publisher)),
            typeof(IEnumerable<IEventHandler<HighSlotLevelBase>>));

        var mediator = new DefaultMediator(resolutions, 2);
        await HighSlot.LevelBase(mediator);
        await HighSlot.LevelBase(mediator); // caches the base level's entry
        await HighSlot.Level(mediator); // takes it, and caches the derived level's and both levels' executors
        var both = publisher.Last;
        await HighSlot.Level(mediator);

        Assert.Same(both, publisher.Last);
        Assert.Equal(2, resolutions.Resolutions);

        Assert.Equal(
            ["base", "base", "level", "base", "level", "base"], resolutions.GetRequiredService<TraceLog>().Entries);

        Assert.IsType<SparseChains>(Assert.Single(Chains(mediator)));
    }

    [Fact]
    // A mediator that caches from its first dispatch, as a Singleton one does, lives as long as its process, which pays
    // for an array of every slot it caches however few they are.
    public void Cache_OfAMediatorThatCachesFromItsFirstDispatch_KeepsEverySlotInItsArray()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 1);
        var chain = new object();
        mediator.Cache(Chains(mediator), 1000, chain);
        Assert.Equal(1001, Chains(mediator).Length);
        Assert.Same(chain, Chains(mediator)[1000]);
    }

    [Fact]
    // A mediator that caches few chains keeps its array within DenseSlots slots, however it grows.
    public void Cache_KeepsTheArrayOfAMediatorThatCachesFewWithinDenseSlots()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        mediator.Cache(Chains(mediator), DefaultMediator.DenseSlots - 12, new object());
        var high = new object();
        mediator.Cache(Chains(mediator), DefaultMediator.DenseSlots, high);
        var low = new object();
        mediator.Cache(Chains(mediator), DefaultMediator.DenseSlots - 11, low);
        Assert.Equal(DefaultMediator.DenseSlots, Chains(mediator).Length);
        Assert.Same(low, Chains(mediator)[DefaultMediator.DenseSlots - 11]);
        Assert.Same(high, mediator.Lookup(DefaultMediator.DenseSlots));
    }

    [Fact]
    // When the table would grow to take more memory than an array of every slot it holds, the mediator moves its chains
    // into such an array, and from then on keeps every chain there. Slots from DenseSlots fill a table of 16 entries at
    // the twelfth; the thirteenth moves them.
    public void Cache_MovesTheChainsIntoAnArray_WhenAnArrayOfTheirSlotsTakesNoMoreMemory()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        var low = new object();
        mediator.Cache(Chains(mediator), 1, low);

        var chains = new object[13];
        for (var i = 0; i < 12; i++)
            mediator.Cache(Chains(mediator), DefaultMediator.DenseSlots + i, chains[i] = new object());

        Assert.Equal(2, Chains(mediator).Length);

        mediator.Cache(Chains(mediator), DefaultMediator.DenseSlots + 12, chains[12] = new object());
        var moved = Chains(mediator);
        Assert.True(moved.Length > DefaultMediator.DenseSlots + 12);
        Assert.Null(moved[0]);
        Assert.Same(low, moved[1]);

        for (var i = 0; i < 13; i++) Assert.Same(chains[i], moved[DefaultMediator.DenseSlots + i]);

        var later = new object();
        mediator.Cache(moved, moved.Length, later);
        Assert.Same(later, Chains(mediator)[moved.Length]);
    }

    [Fact]
    // What a dispatch that misses the array finds: nothing in a mediator that caches nothing, the array's element of a
    // slot inside it, and the table's chain of one past it, if the mediator has a table.
    public void Lookup_SlotInsideArrayOrPastIt_ReadsArrayOrTable()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        Assert.Null(mediator.Lookup(1));

        var chain = new object();
        mediator.Cache(Chains(mediator), 2, chain);
        Assert.Same(chain, mediator.Lookup(2));
        Assert.Null(mediator.Lookup(1));
        Assert.Null(mediator.Lookup(DefaultMediator.DenseSlots));

        var high = new object();
        mediator.Cache(Chains(mediator), DefaultMediator.DenseSlots, high);
        Assert.Same(high, mediator.Lookup(DefaultMediator.DenseSlots));
        Assert.Null(mediator.Lookup(DefaultMediator.DenseSlots + 1));
    }

    [Fact]
    // A mediator that keeps finding a chain in its table, as a long-lived scope does, moves its chains into an array of
    // every slot once those finds number a quarter of the highest slot there, 10 for slot 40, and from then on keeps
    // every chain in its array.
    public void Lookup_MovesTheChainsIntoAnArray_OnceFindsInTheTableReachAQuarterOfTheHighestSlot()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        var low = new object();
        mediator.Cache(Chains(mediator), 1, low);
        var high = new object();
        mediator.Cache(Chains(mediator), 40, high);

        for (var find = 1; find < 10; find++) Assert.Same(high, mediator.Lookup(40));

        Assert.Equal(2, Chains(mediator).Length);
        Assert.Same(high, mediator.Lookup(40));

        var moved = Chains(mediator);
        Assert.True(moved.Length > 40);
        Assert.Null(moved[0]);
        Assert.Same(low, moved[1]);
        Assert.Same(high, moved[40]);

        var later = new object();
        mediator.Cache(moved, 1000, later);
        Assert.Same(later, Chains(mediator)[1000]);
    }

    // ---- a write that another dispatch's growth overtakes -----------------------------------------------------------
    //
    // A dispatch that misses reads the mediator's array, resolves and builds its chain, and only then writes it, so
    // other dispatches can grow the array in between. Each test hands Cache the array a dispatch read, after taking the
    // steps of the dispatches that overtook it, which fixes the interleaving without a thread or a clock.

    [Fact]
    // The write lands in the array that replaced the one the dispatch read, however often it was replaced, beside what
    // the array held, so the next dispatch takes the chain instead of resolving its handler again. The write used to
    // land in the array the dispatch read, which nothing read any more, and the chain was lost.
    public async Task Cache_KeepsAChainWrittenAfterOtherDispatchesGrewTheArray()
    {
        var provider = new CountingServiceProvider(Provider(x => x.AddHandler<GrowthRaceHandler>()), typeof(IRequestHandler<GrowthRace, int>));
        var mediator = new DefaultMediator(provider, 1);
        var slot = RequestSlot<GrowthRace, int>.Index;
        var other = new object();

        mediator.Cache(Chains(mediator), slot + 1, other);
        var read = Chains(mediator);

        mediator.Cache(Chains(mediator), Chains(mediator).Length, new object());
        mediator.Cache(Chains(mediator), Chains(mediator).Length, new object());
        mediator.Cache(read, slot, (MessageHandle<GrowthRace, int>)(static (_, _) => new ValueTask<int>(2)));
        Assert.Equal(2, await mediator.Request<GrowthRace, int>(new GrowthRace()));
        Assert.Equal(0, provider.Resolutions);
        Assert.Same(other, Chains(mediator)[slot + 1]);
    }

    [Fact]
    // A write inside the array takes no lock, so it can land after another dispatch has copied the array to grow it,
    // while the writing dispatch still reads that array as the mediator's and keeps its write there. The growth reads
    // the array again once the copy is the mediator's, and writes the chain into the copy beside what the copy holds.
    public void Replace_KeepsAChainWrittenIntoTheArrayAfterItWasCopied()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        var kept = new object();
        mediator.Cache(Chains(mediator), 3, kept);

        var read = Chains(mediator);
        var copy = new object?[read.Length * 2];
        Array.Copy(read, copy, read.Length);

        var late = new object();
        read[2] = late;
        mediator.Replace(read, copy);
        Assert.Same(copy, Chains(mediator));
        Assert.Same(late, copy[2]);
        Assert.Same(kept, copy[3]);
        Assert.Null(copy[1]);
    }

    [Fact]
    // A dispatch whose slot lies past the end of the array it read grows the array that replaced it, not the one it
    // read, so what the dispatch that replaced it wrote stays.
    public void Cache_GrowsTheArrayThatReplacedTheOneItRead()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 1);
        mediator.Cache(Chains(mediator), 1, new object());

        var read = Chains(mediator);
        var other = new object();
        mediator.Cache(Chains(mediator), read.Length, other);

        var mine = new object();
        var slot = Chains(mediator).Length;
        mediator.Cache(read, slot, mine);
        Assert.Same(other, Chains(mediator)[read.Length]);
        Assert.Same(mine, Chains(mediator)[slot]);
    }

    [Fact]
    // Every mediator starts with the same empty array. Two dispatches that read it each make a first array; the second
    // finds the first's installed and writes into it.
    public void Cache_WritesIntoTheFirstArrayAnotherDispatchInstalled()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 1);
        var read = Chains(mediator);
        var other = new object();
        mediator.Cache(read, 3, other);

        var mine = new object();
        mediator.Cache(read, 1, mine);
        Assert.Same(other, Chains(mediator)[3]);
        Assert.Same(mine, Chains(mediator)[1]);
    }

    [Fact]
    // A first array of nothing but the table: a dispatch that finds it installed adds its chain to the table, or grows
    // the array around it.
    public void Cache_WritesBesideTheFirstArrayAnotherDispatchInstalled()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        var read = Chains(mediator);
        var other = new object();
        mediator.Cache(read, 1000, other);

        var mine = new object();
        mediator.Cache(read, 2000, mine);
        var low = new object();
        mediator.Cache(read, 1, low);
        Assert.Same(other, mediator.Lookup(1000));
        Assert.Same(mine, mediator.Lookup(2000));
        Assert.Same(low, Chains(mediator)[1]);
    }

    [Fact]
    // The table moves with the array: a growth of the array copies it, so a chain written into the table of the array a
    // dispatch read is in the table of the array that replaced it, beside what the table held.
    public void Cache_KeepsAChainWrittenBesideAnArrayAnotherDispatchGrew()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        var other = new object();
        mediator.Cache(Chains(mediator), 1000, other);

        var read = Chains(mediator);
        mediator.Cache(Chains(mediator), 1, new object());
        mediator.Cache(Chains(mediator), Chains(mediator).Length, new object());

        var mine = new object();
        mediator.Cache(read, 2000, mine);
        Assert.Same(other, mediator.Lookup(1000));
        Assert.Same(mine, mediator.Lookup(2000));
    }

    [Fact]
    // A write that finds the chains moved into an array writes into that array, which holds every slot.
    public void Cache_WritesIntoTheArrayThatTheChainsMovedInto()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        for (var i = 0; i < 12; i++) mediator.Cache(Chains(mediator), DefaultMediator.DenseSlots + i, new object());

        var read = Chains(mediator);
        var other = new object();
        mediator.Cache(Chains(mediator), DefaultMediator.DenseSlots + 12, other);

        var mine = new object();
        mediator.Cache(read, DefaultMediator.DenseSlots + 13, mine);
        Assert.Same(other, Chains(mediator)[DefaultMediator.DenseSlots + 12]);
        Assert.Same(mine, Chains(mediator)[DefaultMediator.DenseSlots + 13]);
    }

    [Fact]
    // A find that reaches the count after another dispatch grew the array it read moves the table of the array that
    // replaced it, which holds the same table beside the slot the other dispatch added.
    public void MoveToArray_MovesTheTableOfTheArrayThatReplacedTheOneItRead()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        mediator.Cache(Chains(mediator), 1, new object());

        var high = new object();
        mediator.Cache(Chains(mediator), 40, high);

        var read = Chains(mediator);
        var low = new object();
        mediator.Cache(Chains(mediator), 5, low);
        mediator.MoveToArray(read);

        var moved = Chains(mediator);
        Assert.True(moved.Length > 40);
        Assert.Same(low, moved[5]);
        Assert.Same(high, moved[40]);
    }

    [Fact]
    // An array without a table, as one that its chains have moved into, stays as it is.
    public void MoveToArray_LeavesAnArrayWithoutATable()
    {
        var mediator = new DefaultMediator(Provider(static _ => { }), 2);
        mediator.Cache(Chains(mediator), 1, new object());

        var chains = Chains(mediator);
        mediator.MoveToArray(chains);
        Assert.Same(chains, Chains(mediator));
    }

    private static object?[] Chains(DefaultMediator mediator) =>
        (object?[])typeof(DefaultMediator).GetField("_chains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mediator)!;

    private static void AssertNullMessage(Action dispatch) =>
        Assert.Equal("message", Assert.Throws<ArgumentNullException>(dispatch).ParamName);

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source) items.Add(item);

        return items;
    }

    private sealed class CountingServiceProvider(IServiceProvider inner, params Type[] counted) : IServiceProvider
    {
        public int Resolutions { get; private set; }

        public object? GetService(Type serviceType)
        {
            if (counted.Contains(serviceType)) Resolutions++;

            return inner.GetService(serviceType);
        }
    }
}

public sealed record LaterRequest : IRequest<int>;

public sealed class LaterRequestHandler : IRequestHandler<LaterRequest, int>
{
    public ValueTask<int> Handle(LaterRequest message, CancellationToken cancellationToken) => new(1);
}

public sealed record GrowthRace : IRequest<int>;

public sealed class GrowthRaceHandler : IRequestHandler<GrowthRace, int>
{
    public ValueTask<int> Handle(GrowthRace message, CancellationToken cancellationToken) => new(1);
}

public sealed record LateRequest : IRequest<int>;

public sealed class LateRequestHandler : IRequestHandler<LateRequest, int>
{
    public ValueTask<int> Handle(LateRequest message, CancellationToken cancellationToken) => new(1);
}

// Dispatched only by the test of an empty slot, so its slot is taken after the event's.
public sealed record SizingRequest : IRequest<int>;

public sealed class SizingRequestHandler : IRequestHandler<SizingRequest, int>
{
    public ValueTask<int> Handle(SizingRequest message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
}

// Dispatches of the messages that tests of high slots use, kept out of the tests so that a message takes its slot when
// one of these runs, after the test has taken slots, even where the JIT reads a slot while it compiles the method that
// dispatches the message.
internal static class HighSlot
{
    public static int RequestIndex => Read();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask<int> Request(DefaultMediator mediator) =>
        mediator.Request<HighSlotRequest, int>(new HighSlotRequest());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask Command(DefaultMediator mediator) => mediator.Request(new HighSlotCommand());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IAsyncEnumerable<int> Stream(DefaultMediator mediator) =>
        mediator.RequestStream<HighSlotStream, int>(new HighSlotStream());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask Notice(DefaultMediator mediator) => mediator.Publish(new HighSlotNotice());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask Notice(DefaultMediator mediator, IEventPublisher publisher) =>
        mediator.Publish(new HighSlotNotice(), publisher);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask LevelBase(DefaultMediator mediator) => mediator.Publish(new HighSlotLevelBase());

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ValueTask Level(DefaultMediator mediator) => mediator.Publish(new HighSlotLevel());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Read() => RequestSlot<HighSlotRequest, int>.Index;
}

public sealed record HighSlotRequest : IRequest<int>;

public sealed class HighSlotRequestHandler : IRequestHandler<HighSlotRequest, int>
{
    public ValueTask<int> Handle(HighSlotRequest message, CancellationToken cancellationToken) => new(1);
}

public sealed record HighSlotCommand : IRequest;

public sealed class HighSlotCommandHandler(Counter counter) : IRequestHandler<HighSlotCommand>
{
    public ValueTask Handle(HighSlotCommand message, CancellationToken cancellationToken)
    {
        counter.Increment();
        return ValueTask.CompletedTask;
    }
}

public sealed record HighSlotStream : IStreamRequest<int>;

public sealed class HighSlotStreamHandler : IStreamRequestHandler<HighSlotStream, int>
{
    public async IAsyncEnumerable<int> Handle(
        HighSlotStream request,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        yield return 0;
        await Task.Yield();
        yield return 1;
    }
}

public sealed record HighSlotNotice : IEvent;

public sealed class HighSlotNoticeHandler(TraceLog trace) : IEventHandler<HighSlotNotice>
{
    public ValueTask Handle(HighSlotNotice message, CancellationToken cancellationToken)
    {
        trace.Add("notice");
        return ValueTask.CompletedTask;
    }
}

// An event of two levels, its own and its base class's, and an event of the base class alone.
public record HighSlotLevelBase : IEvent;

public sealed record HighSlotLevel : HighSlotLevelBase;

public sealed class HighSlotLevelBaseHandler(TraceLog trace) : IEventHandler<HighSlotLevelBase>
{
    public ValueTask Handle(HighSlotLevelBase message, CancellationToken cancellationToken)
    {
        trace.Add("base");
        return ValueTask.CompletedTask;
    }
}

public sealed class HighSlotLevelHandler(TraceLog trace) : IEventHandler<HighSlotLevel>
{
    public ValueTask Handle(HighSlotLevel message, CancellationToken cancellationToken)
    {
        trace.Add("level");
        return ValueTask.CompletedTask;
    }
}