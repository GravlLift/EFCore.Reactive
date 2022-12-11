using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;

namespace EFCore.Reactive.Tests
{
    public class ReactiveDbContextTests
    {
        private class TestEntity
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public TestParent? Parent { get; set; }
        }

        private class TestParent
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public List<TestEntity> TestEntities { get; set; } = new();
        }

        private class TestOwnedOneEntity
        {
            public string? Name { get; set; }
        }

        private class TestOwnedManyEntity
        {
            public string? Name { get; set; }
        }

        private class TestOwnerEntity
        {
            public int Id { get; set; }
            public string? Name { get; init; }
            public TestOwnedOneEntity? OwnedOne { get; set; }
            public List<TestOwnedManyEntity> OwnedMany { get; set; } = new();
        }

        private class TestDbContext : ReactiveDbContext
        {
            public TestDbContext(
                DbContextOptions<TestDbContext> options,
                IObservable<EntityChange[]> observable
            ) : base(options, observable) { }

            public DbSet<TestEntity> TestEntities => Set<TestEntity>();
            public DbSet<TestParent> TestCollectionParents => Set<TestParent>();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                base.OnModelCreating(modelBuilder);

                modelBuilder.Entity<TestOwnerEntity>(testOwnerEb =>
                {
                    testOwnerEb.OwnsOne(o => o.OwnedOne).WithOwner();
                    testOwnerEb.OwnsMany(o => o.OwnedMany).WithOwner();
                });
            }
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
                    options.EnableSensitiveDataLogging();
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
            var context1 = provider
                .CreateScope()
                .ServiceProvider.GetRequiredService<TestDbContext>();
            var context2 = provider
                .CreateScope()
                .ServiceProvider.GetRequiredService<TestDbContext>();
            var changeTask = context2.TestEntities.Changes().FirstAsync().ToTask();

            var entity = new TestEntity { Name = "Test" };
            context1.TestEntities.Add(entity);
            context1.SaveChanges();

            var result = await changeTask;
            Assert.Multiple(() =>
            {
                Assert.That(result.Entity.Id, Is.EqualTo(entity.Id));
                Assert.That(result.Entity.Name, Is.EqualTo(entity.Name));
            });
        }

        [Test]
        public void Should_merge_existing()
        {
            var context = provider
                .CreateScope()
                .ServiceProvider.GetRequiredService<TestDbContext>();

            // Add parent
            var child = new TestEntity { Id = 1, Name = "Old" };
            var parent = new TestParent
            {
                Id = 1,
                Name = "Old",
                TestEntities = new List<TestEntity> { child }
            };
            context.Add(parent);

            // Add entities with same PKs
            var newChildWithId = new TestEntity { Id = 2 };
            context.Merge(
                new TestParent
                {
                    Id = 1,
                    Name = "New",
                    TestEntities = new List<TestEntity>
                    {
                        new TestEntity { Id = 1, Name = "New" },
                        newChildWithId
                    }
                }
            );

            Assert.Multiple(() =>
            {
                Assert.That(context.TestCollectionParents.Local, Does.Contain(parent));
                Assert.That(context.TestCollectionParents.Find(1)!.Name, Is.EqualTo("New"));

                Assert.That(context.TestEntities.Local.ToArray(), Does.Contain(child));
                Assert.That(context.TestEntities.Find(1)!.Name, Is.EqualTo("New"));

                Assert.That(context.TestEntities.Local.ToArray(), Does.Contain(newChildWithId));
            });
        }

        [Test]
        public void Should_merge_owned_one_entities()
        {
            var context = provider
                .CreateScope()
                .ServiceProvider.GetRequiredService<TestDbContext>();

            // Add initial
            var initialOwned = new TestOwnedOneEntity { Name = "Initial Owned" };
            var initialOwner = new TestOwnerEntity
            {
                Id = 1,
                Name = "Initial Owner",
                OwnedOne = initialOwned
            };
            context.Add(initialOwner);

            // Add entities with same PKs
            var newOwned = new TestOwnedOneEntity { Name = "New Owned" };
            var newOwner = new TestOwnerEntity
            {
                Id = 1,
                Name = "New Owner",
                OwnedOne = newOwned
            };
            context.Merge(newOwner);

            var owner = context.Set<TestOwnerEntity>().Local.Single();

            Assert.That(
                owner,
                Is.EqualTo(initialOwner),
                "Owner object in context is not the intial object."
            );
            Assert.That(owner.Name, Is.EqualTo("New Owner"));
            Assert.That(
                owner.OwnedOne,
                Is.EqualTo(initialOwned),
                "Owned object in context is not the intial object."
            );
            Assert.That(owner.OwnedOne?.Name, Is.EqualTo("New Owned"));
        }

        [Test]
        public void Should_merge_owned_many_entities()
        {
            var context = provider
                .CreateScope()
                .ServiceProvider.GetRequiredService<TestDbContext>();

            // Add initial
            var initialOwned = new TestOwnedManyEntity { Name = "Initial Owned" };
            var initialOwner = new TestOwnerEntity
            {
                Id = 1,
                Name = "Initial Owner",
                OwnedMany = new List<TestOwnedManyEntity> { initialOwned }
            };
            context.Add(initialOwner);

            // Add entities with same PKs
            var newOwned = new TestOwnedManyEntity { Name = "New Owned" };
            context.Entry(newOwned).Property("Id").CurrentValue = 1;
            var newOwner = new TestOwnerEntity
            {
                Id = 1,
                Name = "New Owner",
                OwnedMany = new List<TestOwnedManyEntity> { newOwned }
            };
            context.Merge(newOwner);

            var owner = context.Set<TestOwnerEntity>().Local.Single();
            Assert.Multiple(() =>
            {
                Assert.That(
                    owner,
                    Is.EqualTo(initialOwner),
                    "Owner object in context is not the intial object."
                );
                Assert.That(owner.Name, Is.EqualTo("New Owner"));
                Assert.That(
                    owner.OwnedMany.Single(),
                    Is.EqualTo(initialOwned),
                    "Owned object in context is not the intial object."
                );
                Assert.That(owner.OwnedMany.Single().Name, Is.EqualTo("New Owned"));
            });
        }

        [Test]
        public void Should_merge_existing_parent()
        {
            var context = provider
                .CreateScope()
                .ServiceProvider.GetRequiredService<TestDbContext>();

            // Add parent
            var firstChild = new TestEntity { Id = 1, Name = "First" };
            var firstParent = new TestParent
            {
                Id = 1,
                Name = "Parent",
                TestEntities = new List<TestEntity> { firstChild }
            };
            context.Add(firstParent);

            var secondParent = new TestParent { Id = 1, Name = "Parent2" };
            var secondChild = new TestEntity
            {
                Id = 2,
                Name = "Second",
                Parent = firstParent
            };

            context.Merge(secondChild);
        }
    }
}
