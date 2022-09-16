namespace EFCore.Reactive
{
    public class Change<TEntity> where TEntity : class
    {
        public ChangeType Type { get; }
        public TEntity Entity { get; }

        public Change(ChangeType type, TEntity entity)
        {
            Type = type;
            Entity = entity;
        }

        public static implicit operator Change<TEntity>(Change<object> other)
        {
            return new Change<TEntity>(other.Type, (TEntity)other.Entity);
        }
    }
}
