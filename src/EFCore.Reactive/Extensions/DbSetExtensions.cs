using EFCore.Reactive;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore
{
    public static class DbSetExtensions
    {
        public static void Merge<TEntity>(this DbSet<TEntity> set, TEntity entity)
            where TEntity : class
        {
            if (set.GetService<ICurrentDbContext>().Context is ReactiveDbContext reactiveDbContext)
            {
                reactiveDbContext.Merge(entity);
            }
            else
            {
                throw new InvalidOperationException(
                    $"{nameof(MergeRange)} can only be performed on {nameof(ReactiveDbContext)} contexts."
                );
            }
        }

        public static void MergeRange<TEntity>(
            this DbSet<TEntity> set,
            IEnumerable<TEntity> entities
        ) where TEntity : class
        {
            if (set.GetService<ICurrentDbContext>().Context is ReactiveDbContext reactiveDbContext)
            {
                foreach (var entity in entities)
                {
                    reactiveDbContext.Merge(entity);
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"{nameof(MergeRange)} can only be performed on {nameof(ReactiveDbContext)} contexts."
                );
            }
        }
    }
}
