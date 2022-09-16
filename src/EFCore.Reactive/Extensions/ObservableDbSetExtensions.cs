using System;
using System.Collections.Generic;
using EFCore.Reactive;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore
{
    public static class ObservableDbSetExtensions
    {
        public static IObservable<IReadOnlyCollection<Change<TEntity>>> Changes<TEntity>(
            this DbSet<TEntity> dbSet
        ) where TEntity : class
        {
            var context = (ReactiveDbContext)dbSet.GetService<ICurrentDbContext>().Context;
            return context.Changes<TEntity>();
        }
    }
}
