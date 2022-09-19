using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Internal;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Collections;
using System.Reflection;

namespace EFCore.Reactive
{
    public abstract class ReactiveDbContext : DbContext, IDisposable
    {
        private Subject<IEnumerable<object>> changes = new();
        private IDisposable subscription;
        private readonly Dictionary<Type, ConstructorInfo> changeConstructors = new();

        protected ReactiveDbContext(
            DbContextOptions options,
            IObservable<EntityChange[]> observable
        ) : base(options)
        {
            subscription = observable.Subscribe((changes) => OnDbContextChanges(changes));
        }

        internal IObservable<IReadOnlyCollection<Change<T>>> Changes<T>() where T : class =>
            changes.Select(changes => changes.OfType<Change<T>>().ToArray());

        private void OnDbContextChanges(EntityChange[] changes)
        {
            try
            {
                #region Parse Changes and write to DbContext
                List<object> modifiedObjects = new();
                foreach (var change in changes)
                {
                    var existingEntry = FindTracked(change.EntityTypeName, change.KeyValues);
                    var entityType = Model.FindEntityType(change.EntityTypeName);

                    if (entityType == null)
                    {
                        throw new InvalidOperationException(
                            $"Entity type name '{change.EntityTypeName}' does not have an associated entity type"
                        );
                    }

                    switch (change.ChangeType)
                    {
                        case EntityState.Added:
                        {
                            if (existingEntry == null)
                            {
                                EntityEntry? newEntityEntry;
                                if (
                                    change
                                    is ObserverSaveChangesInterceptor.PropertiesChange propertiesChange
                                )
                                {
                                    if (entityType.ConstructorBinding == null)
                                    {
                                        throw new NotImplementedException();
                                    }

                                    // Create a clone of the object in memory
                                    var constructorExpression =
                                        entityType.ConstructorBinding.CreateConstructorExpression(
                                            new(entityType, null!)
                                        );
                                    var entity =
                                        constructorExpression is NewExpression newExpression
                                        && newExpression.Constructor != null
                                            ? newExpression.Constructor.Invoke(null)
                                            : throw new NotImplementedException();

                                    newEntityEntry = Entry(entity);
                                    SetEntityEntryValues(propertiesChange, newEntityEntry);
                                    newEntityEntry = Add(newEntityEntry.Entity);
                                }
                                else if (
                                    change
                                    is ObserverSaveChangesInterceptor.SkipNavigationChange skipNavigationChange
                                )
                                {
                                    var declaringEntityType = Model.FindEntityType(
                                        skipNavigationChange.DeclaringEntityTypeName
                                    );
                                    var declaringEntityEntry = FindTracked(
                                        skipNavigationChange.DeclaringEntityTypeName,
                                        skipNavigationChange.DeclaringKeyValues
                                    );
                                    if (declaringEntityEntry == null)
                                    {
                                        break;
                                    }

                                    var targetEntityType = Model.FindEntityType(
                                        skipNavigationChange.TargetEntityTypeName
                                    );

                                    var targetEntityEntry = FindTracked(
                                        skipNavigationChange.TargetEntityTypeName,
                                        skipNavigationChange.TargetKeyValues
                                    );
                                    if (targetEntityEntry == null)
                                    {
                                        break;
                                    }

                                    var navigation = declaringEntityEntry.Navigation(
                                        skipNavigationChange.NavigationPropertyName
                                    );

                                    if (navigation is CollectionEntry collectionEntry)
                                    {
                                        if (collectionEntry.CurrentValue == null)
                                        {
                                            // Create an empty collection
                                            collectionEntry.CurrentValue = (IEnumerable)
                                                typeof(List<>)
                                                    .MakeGenericType(targetEntityType!.ClrType)
                                                    .GetConstructor(Array.Empty<Type>())!
                                                    .Invoke(Array.Empty<object>());
                                        }
                                        // Add the incoming object to the new collection
                                        collectionEntry.CurrentValue
                                            .GetType()
                                            .GetMethod(nameof(ICollection<object>.Add))!
                                            .Invoke(
                                                collectionEntry.CurrentValue,
                                                new[] { targetEntityEntry.Entity }
                                            );

                                        newEntityEntry = FindTracked(
                                            skipNavigationChange.EntityTypeName,
                                            skipNavigationChange.DeclaringKeyValues
                                                .Concat(skipNavigationChange.TargetKeyValues)
                                                .ToArray()
                                        );

                                        if (newEntityEntry == null)
                                        {
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        throw new NotImplementedException();
                                    }
                                }
                                else
                                {
                                    throw new NotImplementedException();
                                }
                                newEntityEntry.State = EntityState.Unchanged;

                                var type = newEntityEntry.Entity.GetType();
                                var constructor = changeConstructors.GetOrAdd(
                                    type,
                                    () =>
                                        typeof(Change<>)
                                            .MakeGenericType(type)
                                            .GetConstructor(
                                                new Type[] { typeof(ChangeType), type }
                                            )!
                                );
                                modifiedObjects.Add(
                                    constructor.Invoke(
                                        new object[]
                                        {
                                            ChangeType.CreateOrUpdate,
                                            newEntityEntry.Entity
                                        }
                                    )
                                );
                            }
                            break;
                        }
                        case EntityState.Deleted:
                            if (existingEntry != null)
                            {
                                Remove(existingEntry.Entity);
                                existingEntry.State = EntityState.Detached;

                                var type = existingEntry.Entity.GetType();
                                var constructor = changeConstructors.GetOrAdd(
                                    type,
                                    () =>
                                        typeof(Change<>)
                                            .MakeGenericType(type)
                                            .GetConstructor(
                                                new Type[] { typeof(ChangeType), type }
                                            )!
                                );
                                modifiedObjects.Add(
                                    constructor.Invoke(
                                        new object[]
                                        {
                                            ChangeType.CreateOrUpdate,
                                            existingEntry.Entity
                                        }
                                    )
                                );
                            }
                            break;
                        case EntityState.Modified:
                        {
                            if (
                                existingEntry != null
                                && change
                                    is ObserverSaveChangesInterceptor.PropertiesChange propertiesChange
                            )
                            {
                                SetEntityEntryValues(propertiesChange, existingEntry);
                                existingEntry.State = EntityState.Unchanged;

                                var type = existingEntry.Entity.GetType();
                                var constructor = changeConstructors.GetOrAdd(
                                    type,
                                    () =>
                                        typeof(Change<>)
                                            .MakeGenericType(type)
                                            .GetConstructor(
                                                new Type[] { typeof(ChangeType), type }
                                            )!
                                );
                                modifiedObjects.Add(
                                    constructor.Invoke(
                                        new object[]
                                        {
                                            ChangeType.CreateOrUpdate,
                                            existingEntry.Entity
                                        }
                                    )
                                );
                            }
                            break;
                        }
                    }
                }
                #endregion

                this.changes.OnNext(modifiedObjects);
            }
            catch (ObjectDisposedException)
            {
                subscription?.Dispose();
            }
        }

        private static void SetEntityEntryValues(
            ObserverSaveChangesInterceptor.PropertiesChange change,
            EntityEntry existingEntry
        )
        {
            foreach (var changedMember in change.ChangedProperties)
            {
                var propertyType = existingEntry.CurrentValues.Properties
                    .Single(p => p.Name == changedMember.Key)
                    .ClrType.GetUnderlyingNullableOrType();
                if (changedMember.Value == null)
                {
                    existingEntry.CurrentValues[changedMember.Key] = null;
                }
                else if (propertyType.IsEnum)
                {
                    existingEntry.CurrentValues[changedMember.Key] = Enum.ToObject(
                        propertyType,
                        changedMember.Value
                    );
                }
                else if (propertyType == typeof(DateTimeOffset))
                {
                    if (changedMember.Value is DateTime dateTime)
                    {
                        existingEntry.CurrentValues[changedMember.Key] = new DateTimeOffset(
                            dateTime
                        );
                    }
                    else
                    {
                        existingEntry.CurrentValues[changedMember.Key] = Convert.ChangeType(
                            changedMember.Value,
                            propertyType
                        );
                    }
                }
                else if (propertyType == typeof(TimeSpan))
                {
                    if (changedMember.Value is string str)
                    {
                        existingEntry.CurrentValues[changedMember.Key] = TimeSpan.Parse(str);
                    }
                    else
                    {
                        existingEntry.CurrentValues[changedMember.Key] = Convert.ChangeType(
                            changedMember.Value,
                            propertyType
                        );
                    }
                }
                else
                {
                    existingEntry.CurrentValues[changedMember.Key] = Convert.ChangeType(
                        changedMember.Value,
                        propertyType
                    );
                }

                existingEntry.OriginalValues[changedMember.Key] = existingEntry.CurrentValues[
                    changedMember.Key
                ];
            }
        }

#pragma warning disable EF1001 // Internal EF Core API usage.
        /// <summary>
        /// Search the context's local storage for an existing entity with matching key values to the search object.
        /// </summary>
        /// <param name="entry">An entry sharing the same primary key value as the entity to search for</param>
        /// <returns>The EntityEntry of the matching object, or null if it is not being tracked</returns>
        private EntityEntry? FindTracked(string entityTypeName, object[] keyValues)
        {
            var entityType = Model.FindEntityType(entityTypeName);
            if (entityType == null)
            {
                throw new ArgumentException(
                    $"Could not find entity type {entityTypeName}",
                    nameof(entityTypeName)
                );
            }

            var key = entityType.FindPrimaryKey();
            if (key == null)
            {
                throw new ArgumentException(
                    $"Entity type {entityTypeName} does not have a primary key",
                    nameof(entityTypeName)
                );
            }

            for (var i = 0; i < key.Properties.Count; i++)
            {
                if (key.Properties[i].ClrType != keyValues[i].GetType())
                {
                    keyValues[i] = Convert.ChangeType(keyValues[i], key.Properties[i].ClrType);
                }
            }
            var stateManager = this.GetDependencies().StateManager;
            var internalEntry = stateManager.TryGetEntry(key, keyValues);
            return internalEntry != null ? Entry(internalEntry.Entity) : null;
        }
#pragma warning restore EF1001 // Internal EF Core API usage.

        private void Dispose(bool disposing)
        {
            base.Dispose();
            if (subscription != null)
            {
                if (disposing)
                {
                    subscription.Dispose();
                }

                subscription = null!;
            }

            if (changes != null)
            {
                if (disposing)
                {
                    changes.Dispose();
                }

                changes = null;
            }
        }

        public override void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
