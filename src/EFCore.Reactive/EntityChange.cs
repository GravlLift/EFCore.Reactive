using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using static EFCore.Reactive.ObserverSaveChangesInterceptor;

namespace EFCore.Reactive
{
    public abstract class EntityChange
    {
        public object[] KeyValues { get; }

        public EntityState ChangeType { get; }

        public string EntityTypeName { get; }

        protected EntityChange(object[] keyValues, EntityState changeType, string entityTypeName)
        {
            KeyValues = keyValues;
            ChangeType = changeType;
            EntityTypeName = entityTypeName;
        }

        public static EntityChange Create(
            DbContext context,
            EntityEntry entityEntry,
            EntityState changeType
        )
        {
            var entityType = context.Model.FindEntityType(entityEntry.Metadata.Name);

            var skipNavigation = context.Model
                .GetEntityTypes()
                .SelectMany(e => e.GetSkipNavigations())
                .FirstOrDefault(s => s.JoinEntityType == entityType);

            if (skipNavigation == null)
            {
                return new PropertiesChange(
                    keyValues: entityEntry.Metadata
                        .FindPrimaryKey()!
                        .Properties.Select(p =>
                        {
                            var property = entityEntry.Property(p.Name);
                            if (property?.CurrentValue == null)
                            {
                                throw new NotImplementedException();
                            }
                            return property.CurrentValue;
                        })
                        .ToArray(),
                    changeType: changeType,
                    entityTypeName: entityEntry.Metadata.Name,
                    changedProperties: changeType switch
                    {
                        EntityState.Added
                            => entityEntry.Properties.ToDictionary(
                                p => p.Metadata.Name,
                                p => p.CurrentValue
                            ),
                        EntityState.Deleted => new(),
                        EntityState.Modified
                            => entityEntry.Properties.ToDictionary(
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
                var primaryKey = entityEntry.Metadata.FindPrimaryKey();
                if (primaryKey == null)
                {
                    throw new NotImplementedException();
                }
                return new SkipNavigationChange(
                    keyValues: primaryKey.Properties
                        .Select(p =>
                        {
                            var property = entityEntry.Property(p.Name);
                            if (property?.CurrentValue == null)
                            {
                                throw new NotImplementedException();
                            }
                            return property.CurrentValue;
                        })
                        .ToArray(),
                    changeType: changeType,
                    entityTypeName: entityEntry.Metadata.Name,
                    declaringEntityTypeName: skipNavigation.DeclaringEntityType.Name,
                    declaringKeyValues: skipNavigation.ForeignKey.Properties
                        .Select(p =>
                        {
                            var property = entityEntry.Property(p.Name);
                            if (property?.CurrentValue == null)
                            {
                                throw new NotImplementedException();
                            }
                            return property.CurrentValue;
                        })
                        .ToArray(),
                    targetEntityTypeName: skipNavigation.TargetEntityType.Name,
                    targetKeyValues: entityEntry.Properties
                        .Where(
                            p =>
                                !skipNavigation.ForeignKey.Properties.Any(
                                    skipProp => skipProp == p.Metadata
                                )
                        )
                        .Select(p =>
                        {
                            var property = entityEntry.Property(p.Metadata.Name);
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
    }
}
