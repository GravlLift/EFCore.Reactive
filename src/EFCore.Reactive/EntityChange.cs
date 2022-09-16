using Microsoft.EntityFrameworkCore;

namespace EFCore.Reactive
{
    public abstract class EntityChange
    {
        public object[] KeyValues { get; }

        public EntityState ChangeType { get; }

        public string EntityTypeName { get; }

        protected EntityChange(
            object[] keyValues,
            EntityState changeType,
            string entityTypeName
        )
        {
            KeyValues = keyValues;
            ChangeType = changeType;
            EntityTypeName = entityTypeName;
        }
    }
}
