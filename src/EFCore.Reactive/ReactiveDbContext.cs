﻿using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

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

        internal IObservable<Change<T>> Changes<T>() where T : class =>
            changes.SelectMany(changes => changes.OfType<Change<T>>());

        internal IObservable<ICollection<T>> Collection<T>() where T : class =>
            Changes<T>()
                .Where(c => c.Type == ChangeType.Create || c.Type == ChangeType.Delete)
                .Select(_ => Set<T>().Local);

        public void Merge(object entity)
        {
            ChangeTracker.TrackGraph(
                entity,
                (EntityEntryGraphNode node) =>
                {
                    var primaryKey = node.Entry.Metadata.FindPrimaryKey();
                    if (primaryKey == null)
                    {
                        throw new NotImplementedException();
                    }

                    var existingEntity = FindTracked(
                        node.Entry.Metadata.Name,
                        primaryKey.Properties
                            .Select(
                                (p) =>
                                {
                                    object? currentValue;
                                    if (node.Entry.Metadata.IsOwned() && p.IsForeignKey())
                                    {
                                        var ownerPk = p.GetContainingForeignKeys()
                                            .Single()
                                            .PrincipalKey;
                                        currentValue = node.SourceEntry!
                                            .Property(ownerPk.Properties.Single().Name)
                                            .CurrentValue;
                                    }
                                    else
                                    {
                                        currentValue = node.Entry.Property(p.Name).CurrentValue;
                                    }

#pragma warning disable EF1001 // Internal EF Core API usage.
                                    if (
                                        currentValue?.GetType().IsDefaultValue(currentValue)
                                        != false
                                    )
#pragma warning restore EF1001 // Internal EF Core API usage.
                                    {
                                        {
                                            throw new InvalidOperationException(
                                                $"Value for primary key {node.Entry.Metadata.Name}.{p.Name} has not been set"
                                            );
                                        }
                                    }
                                    return currentValue!;
                                }
                            )
                            .ToArray()
                    );

                    if (existingEntity == null)
                    {
                        node.Entry.State = EntityState.Added;
                    }
                    else
                    {
                        foreach (var navigation in node.Entry.Navigations)
                        {
                            if (navigation is ReferenceEntry referenceEntry)
                            {
                                var referencePk =
                                    referenceEntry.Metadata.TargetEntityType.FindPrimaryKey();
                                if (referencePk == null)
                                {
                                    throw new NotImplementedException();
                                }
                                var existingReferenceEntity = FindTracked(
                                    referenceEntry.Metadata.TargetEntityType.Name,
                                    referencePk.Properties
                                        .Select(
                                            (p) =>
                                            {
                                                var ownerPk = p.GetContainingForeignKeys()
                                                    .Single()
                                                    .PrincipalKey.Properties.Single();
                                                return node.Entry
                                                    .Property(ownerPk.Name)
                                                    .CurrentValue!;
                                            }
                                        )
                                        .ToArray()
                                );
                                // If reference is to be merged, continue to point to the existing object,
                                // if reference does not yet exist, assign to the new object
                                if (existingReferenceEntity == null)
                                {
                                    existingEntity
                                        .Reference(referenceEntry.Metadata.Name)
                                        .CurrentValue = navigation.CurrentValue;
                                }
                            }
                        }
                        existingEntity.CurrentValues.SetValues(node.Entry.CurrentValues);
                        existingEntity.State = EntityState.Modified;
                    }
                },
                n =>
                {
                    if (n.Entry.State != EntityState.Detached)
                    {
                        return false;
                    }

                    n.NodeState!(n);

                    return true;
                }
            );
        }

        private void MergeInternal(object? entity, HashSet<object> entitiesTraversed)
        {
            if (entity == null)
            {
                return;
            }

            entitiesTraversed.Add(entity);

            var type = entity.GetType();

            if (type.IsAssignableTo(typeof(IEnumerable)))
            {
                foreach (var item in (IEnumerable)entity)
                {
                    MergeInternal(item, entitiesTraversed);
                }
                return;
            }

            var entityType = Model.FindRuntimeEntityType(type);

            if (entityType == null)
            {
                return;
            }

            var primaryKey = entityType.FindPrimaryKey();

            if (primaryKey == null)
            {
                throw new InvalidOperationException(
                    $"Type '{type.Name} does not have an associated entity type"
                );
            }

            var primaryKeyValues = primaryKey.Properties
                .Where(keyProp => keyProp.PropertyInfo != null)
                .Select(keyProp =>
                {
                    var value = keyProp.PropertyInfo!.GetValue(entity);
                    if (value == null)
                    {
                        throw new NotImplementedException(
                            $"{entityType.Name}.{keyProp.Name} does not have a value"
                        );
                    }
                    return value;
                });

            var entityEntry = FindTracked(entityType.Name, primaryKeyValues.ToArray());

            if (entityEntry == null)
            {
                entityEntry = Entry(entity);
                entityEntry.State = EntityState.Added;
            }
            else
            {
                // Entity exists already, update the values
                var properties = entityType
                    .GetProperties()
                    .Where(prop => prop.PropertyInfo != null)
                    .ToDictionary(
                        prop => prop.PropertyInfo!.Name,
                        prop => prop.PropertyInfo!.GetValue(entity)
                    );

                SetEntityEntryValues(entityEntry, properties);
            }

            foreach (var navigation in entityType.GetNavigations())
            {
                if (navigation.PropertyInfo == null)
                {
                    throw new InvalidOperationException();
                }

                var navigationValue = navigation.PropertyInfo.GetValue(entity);

                if (navigationValue != null && !entitiesTraversed.Contains(navigationValue))
                {
                    MergeInternal(navigationValue, entitiesTraversed);
                }
            }
        }

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
                                    SetEntityEntryValues(
                                        newEntityEntry,
                                        propertiesChange.ChangedProperties
                                    );
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
                                        // Create an empty collection
                                        collectionEntry.CurrentValue ??= (IEnumerable)
                                            typeof(List<>)
                                                .MakeGenericType(targetEntityType!.ClrType)
                                                .GetConstructor(Array.Empty<Type>())!
                                                .Invoke(Array.Empty<object>());
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
                                        new object[] { ChangeType.Create, newEntityEntry.Entity }
                                    )
                                );
                            }
                            else
                            {
                                if (
                                    change
                                    is ObserverSaveChangesInterceptor.PropertiesChange propertiesChange
                                )
                                {
                                    // Notify about property adds, even if it was me who did the add
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
                                            new object[] { ChangeType.Create, existingEntry.Entity }
                                        )
                                    );
                                }
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
                                        new object[] { ChangeType.Delete, existingEntry.Entity }
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
                                SetEntityEntryValues(
                                    existingEntry,
                                    propertiesChange.ChangedProperties
                                );
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
                                        new object[] { ChangeType.Update, existingEntry.Entity }
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
            EntityEntry existingEntry,
            Dictionary<string, object?> changes
        )
        {
            foreach (var changedMember in changes)
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
            else if (key.Properties.Count != keyValues.Length)
            {
                throw new NotImplementedException();
            }

            for (var i = 0; i < key.Properties.Count; i++)
            {
                if (key.Properties[i].ClrType != keyValues[i].GetType())
                {
                    keyValues[i] = Convert.ChangeType(keyValues[i], key.Properties[i].ClrType);
                }
            }
#pragma warning disable EF1001 // Internal EF Core API usage.
            var stateManager = this.GetDependencies().StateManager;
            var internalEntry = stateManager.TryGetEntry(key, keyValues);
            return internalEntry != null ? Entry(internalEntry.Entity) : null;
#pragma warning restore EF1001 // Internal EF Core API usage.
        }

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

                changes = null!;
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
