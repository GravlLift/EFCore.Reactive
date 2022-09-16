using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Collections;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reflection;

namespace EFCore.Reactive
{
    public sealed class ChangeReceiver
    {
        public IObservable<Change<dynamic>[]> Changes { get; private set; }

        private readonly DbContext context;

        public ChangeReceiver(DbContext context, IObservable<EntityChange[]> observable)
        {
            this.context = context;
            Changes = observable
                .Select(OnDbContextChanges)
                .Catch((ObjectDisposedException ex) => Observable.Empty<Change<dynamic>[]>());
        }

        private Change<dynamic>[] OnDbContextChanges(EntityChange[] changes)
        {
                #region Parse Changes and write to DbContext
            List<Change<dynamic>> modifiedObjects = new();
            foreach (var change in changes)
            {
                var existingEntry = FindTracked(change.EntityTypeName, change.KeyValues);
                var entityType = context.Model.FindEntityType(change.EntityTypeName);

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

                                newEntityEntry = context.Entry(entity);
                                SetEntityEntryValues(propertiesChange, newEntityEntry);
                                newEntityEntry = context.Add(newEntityEntry.Entity);
                            }
                            else if (
                                change
                                is ObserverSaveChangesInterceptor.SkipNavigationChange skipNavigationChange
                            )
                            {
                                var declaringEntityType = context.Model.FindEntityType(
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

                                var targetEntityType = context.Model.FindEntityType(
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

                            modifiedObjects.Add(
                                new Change<dynamic>(
                                    ChangeType.CreateOrUpdate,
                                    newEntityEntry.Entity
                                )
                            );
                        }
                        break;
                    }
                    case EntityState.Deleted:
                        if (existingEntry != null)
                        {
                            context.Remove(existingEntry.Entity);
                            existingEntry.State = EntityState.Detached;

                            modifiedObjects.Add(
                                new Change<dynamic>(ChangeType.Delete, existingEntry.Entity)
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

                            modifiedObjects.Add(
                                new Change<dynamic>(ChangeType.CreateOrUpdate, existingEntry.Entity)
                            );
                        }
                        break;
                    }
                }
            }
                #endregion
            return modifiedObjects.ToArray();
        }

        private static Func<Change<object>, Change<TResult>> GetChangeConvertFunction<TResult>()
            where TResult : class => c => new Change<TResult>(c.Type, (TResult)c.Entity);

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
            var entityType = context.Model.FindEntityType(entityTypeName);
            if (entityType == null)
                throw new ArgumentException(
                    $"Could not find entity type {entityTypeName}",
                    nameof(entityTypeName)
                );

            var key = entityType.FindPrimaryKey();
            if (key == null)
                throw new ArgumentException(
                    $"Entity type {entityTypeName} does not have a primary key",
                    nameof(entityTypeName)
                );

            for (var i = 0; i < key.Properties.Count; i++)
            {
                if (key.Properties[i].ClrType != keyValues[i].GetType())
                {
                    keyValues[i] = Convert.ChangeType(keyValues[i], key.Properties[i].ClrType);
                }
            }
            var stateManager = context.GetDependencies().StateManager;
            var internalEntry = stateManager.TryGetEntry(key, keyValues);
            return internalEntry != null ? context.Entry(internalEntry.Entity) : null;
        }
#pragma warning restore EF1001 // Internal EF Core API usage.
    }
}
