using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;

namespace EFCore.Reactive.Tests
{
    public class ReactiveDbContextTests
    {
        private class TestEntity
        {
            public int Id { get; set; }
            public string? Name { get; set; }
        }

        private class TestDbContext : ReactiveDbContext
        {
            public TestDbContext(
                DbContextOptions<TestDbContext> options,
                IObservable<EntityChange[]> observable
            ) : base(options, observable) { }

            public DbSet<TestEntity> TestEntities => Set<TestEntity>();
        }

        private readonly IServiceProvider provider;

        public ReactiveDbContextTests()
        {
            Subject<EntityChange[]> inMemorySubject = new();
            var services = new ServiceCollection().AddReactiveDbContext<TestDbContext>(
                inMemorySubject,
                options =>
                {
                    options.UseInMemoryDatabase(TestContext.CurrentContext.Test.FullName);
                }
            );

            provider = services.BuildServiceProvider();
        }

        [Test]
        public void Should_send_added_object_to_other_context()
        {
            var context1 = provider
                .CreateScope()
                .ServiceProvider.GetRequiredService<TestDbContext>();
            var context2 = provider
                .CreateScope()
                .ServiceProvider.GetRequiredService<TestDbContext>();

            context1.TestEntities.Add(new() { Name = "Test" });
            context1.SaveChanges();

            Assert.That(context2.TestEntities.Local, Has.Exactly(1).Items);
        }

        [Test]
        public async Task Should_send_object_to_listener()
        {
            var context = provider
                .CreateScope()
                .ServiceProvider.GetRequiredService<TestDbContext>();
            var changeTask = context.TestEntities.Changes().ToTask();

            context.TestEntities.Add(new() { Name = "Test" });
            context.SaveChanges();

            var result = await changeTask;
            Assert.That(result, Has.Exactly(1).Items);
        }
    }
}
