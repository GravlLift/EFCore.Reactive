using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFCore.Reactive
{
    internal partial class ObserverSaveChangesInterceptor : SaveChangesInterceptor
    {
        private PreSaveChangedEntity[]? preSaveChanges;
        private readonly IObserver<EntityChange[]> observer;

        public ObserverSaveChangesInterceptor(
            IObserver<EntityChange[]> observer
        )
        {
            this.observer = observer;
        }

        #region SavingChanges
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result
        )
        {
            if (eventData.Context != null)
            {
                RecordPresaveChanges(eventData.Context);
            }
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (eventData.Context != null)
            {
                RecordPresaveChanges(eventData.Context);
            }
            return ValueTask.FromResult(result);
        }

        private void RecordPresaveChanges(DbContext context)
        {
            preSaveChanges = context.ChangeTracker
                .Entries()
                .Where(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached)
                .Select(
                    e =>
                        new PreSaveChangedEntity(
                            changeType: e.State,
                            entry: e,
                            changedPropertyNames: e.State switch
                            {
                                EntityState.Added
                                  => e.Properties.Select(p => p.Metadata.Name).ToArray(),
                                EntityState.Deleted => Array.Empty<string>(),
                                EntityState.Modified
                                  => e.Properties
                                      .Where(p => p.IsModified)
                                      .Select(p => p.Metadata.Name)
                                      .ToArray(),
                                _ => throw new NotImplementedException()
                            }
                        )
                )
                .ToArray();
        }

        private class PreSaveChangedEntity
        {
            public EntityState ChangeType { get; }

            public EntityEntry? Entry { get; }

            public string[] ChangedPropertyNames { get; }

            public PreSaveChangedEntity(
                EntityState changeType,
                EntityEntry? entry,
                string[] changedPropertyNames
            )
            {
                ChangeType = changeType;
                Entry = entry;
                ChangedPropertyNames = changedPropertyNames;
            }
        }

        #endregion

        #region SavedChanges
        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            if (eventData.Context != null)
            {
                var changes = GetPostSaveChanges(eventData.Context);

                observer.OnNext(changes);
            }
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            if (eventData.Context != null)
            {
                var changes = GetPostSaveChanges(eventData.Context);

                observer.OnNext(changes);
            }
            return ValueTask.FromResult(result);
        }

        private EntityChange[] GetPostSaveChanges(DbContext context)
        {
            if (preSaveChanges == null)
            {
                throw new InvalidOperationException();
            }

            return preSaveChanges
                .Where(c => c.Entry != null)
                .Select<PreSaveChangedEntity, EntityChange>(
                    c =>
                    {
                        var entityType = context.Model.FindEntityType(c.Entry!.Metadata.Name);

                        var skipNavigation = context.Model
                            .GetEntityTypes()
                            .SelectMany(e => e.GetSkipNavigations())
                            .FirstOrDefault(s => s.JoinEntityType == entityType);

                        if (skipNavigation == null)
                        {
                            return new PropertiesChange(
                                changeType: c.ChangeType,
                                keyValues: c.Entry.Metadata.FindPrimaryKey()!.Properties
                                    .Select(p =>
                                    {
                                        var property = c.Entry.Property(p.Name);
                                        if (property?.CurrentValue == null)
                                        {
                                            throw new NotImplementedException();
                                        }
                                        return property.CurrentValue;
                                    })
                                    .ToArray(),
                                entityTypeName: c.Entry.Metadata.Name,
                                changedProperties: c.ChangeType switch
                                {
                                    EntityState.Added
                                      => c.Entry.Properties.ToDictionary(
                                          p => p.Metadata.Name,
                                          p => p.CurrentValue
                                      ),
                                    EntityState.Deleted => new(),
                                    EntityState.Modified
                                      => c.Entry.Properties.ToDictionary(
                                          p => p.Metadata.Name,
                                          p => p.CurrentValue
                                      ),
                                    _ => throw new NotImplementedException()
                                }
                            );
                        }
                        else
                        {
                            var skipKeys = skipNavigation.ForeignKey;
                            var primaryKey = c.Entry.Metadata.FindPrimaryKey();
                            if (primaryKey == null)
                            {
                                throw new NotImplementedException();
                            }
                            return new SkipNavigationChange(
                                changeType: c.ChangeType,
                                keyValues: primaryKey.Properties
                                    .Select(p =>
                                    {
                                        var property = c.Entry.Property(p.Name);
                                        if (property?.CurrentValue == null)
                                        {
                                            throw new NotImplementedException();
                                        }
                                        return property.CurrentValue;
                                    })
                                    .ToArray(),
                                entityTypeName: c.Entry.Metadata.Name,
                                declaringEntityTypeName: skipNavigation.DeclaringEntityType.Name,
                                targetKeyValues: c.Entry.Properties
                                    .Where(
                                        p =>
                                            !skipNavigation.ForeignKey.Properties.Any(
                                                skipProp => skipProp == p.Metadata
                                            )
                                    )
                                    .Select(p =>
                                    {
                                        var property = c.Entry.Property(p.Metadata.Name);
                                        if (property?.CurrentValue == null)
                                        {
                                            throw new NotImplementedException();
                                        }
                                        return property.CurrentValue;
                                    })
                                    .ToArray(),
                                targetEntityTypeName: skipNavigation.TargetEntityType.Name,
                                declaringKeyValues: skipNavigation.ForeignKey.Properties
                                    .Select(p =>
                                    {
                                        var property = c.Entry.Property(p.Name);
                                        if (property?.CurrentValue == null)
                                        {
                                            throw new NotImplementedException();
                                        }
                                        return property.CurrentValue;
                                    })
                                    .ToArray(),
                                navigationPropertyName: skipNavigation.PropertyInfo!.Name
                            );
                        }
                    }
                )
                .ToArray();
        }

        internal class PropertiesChange : EntityChange
        {
            public PropertiesChange(
                object[] keyValues,
                EntityState changeType,
                string entityTypeName,
                Dictionary<string, object?> changedProperties
            ) : base(keyValues, changeType, entityTypeName)
            {
                ChangedProperties = changedProperties;
            }

            public Dictionary<string, object?> ChangedProperties { get; }
        }

        internal class SkipNavigationChange : EntityChange
        {
            public SkipNavigationChange(
                object[] keyValues,
                EntityState changeType,
                string entityTypeName,
                string declaringEntityTypeName,
                object[] declaringKeyValues,
                string targetEntityTypeName,
                object[] targetKeyValues,
                string navigationPropertyName
            ) : base(keyValues, changeType, entityTypeName)
            {
                DeclaringEntityTypeName = declaringEntityTypeName;
                DeclaringKeyValues = declaringKeyValues;
                TargetEntityTypeName = targetEntityTypeName;
                TargetKeyValues = targetKeyValues;
                NavigationPropertyName = navigationPropertyName;
            }

            public string DeclaringEntityTypeName { get; }
            public object[] DeclaringKeyValues { get; }
            public string TargetEntityTypeName { get; }
            public object[] TargetKeyValues { get; }
            public string NavigationPropertyName { get; }
        }
        #endregion
    }
}
