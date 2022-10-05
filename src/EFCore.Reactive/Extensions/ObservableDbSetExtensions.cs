using EFCore.Reactive;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore
{
    public static class ObservableDbSetExtensions
    {
        public static IObservable<Change<TEntity>> Changes<TEntity>(this DbSet<TEntity> dbSet)
            where TEntity : class =>
            ((ReactiveDbContext)dbSet.GetService<ICurrentDbContext>().Context).Changes<TEntity>();

        public static IObservable<ICollection<TEntity>> Collection<TEntity>(
            this DbSet<TEntity> dbSet
        ) where TEntity : class =>
            (
                (ReactiveDbContext)dbSet.GetService<ICurrentDbContext>().Context
            ).Collection<TEntity>();
    }
}
