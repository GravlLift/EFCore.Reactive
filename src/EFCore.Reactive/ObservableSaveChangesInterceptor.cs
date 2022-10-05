using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFCore.Reactive
{
    internal partial class ObserverSaveChangesInterceptor : SaveChangesInterceptor
    {
        private PreSaveChangedEntity[]? preSaveChanges;
        private readonly IObserver<EntityChange[]> observer;

        public ObserverSaveChangesInterceptor(IObserver<EntityChange[]> observer)
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
                .Select(c => EntityChange.Create(context, c.Entry!, c.ChangeType))
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
