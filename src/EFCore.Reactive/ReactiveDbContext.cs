using Microsoft.EntityFrameworkCore;
using System.Reactive.Linq;

namespace EFCore.Reactive
{
    public abstract class ReactiveDbContext : DbContext
    {
        private readonly ChangeReceiver changeReceiver;

        protected ReactiveDbContext(
            DbContextOptions options,
            IObservable<EntityChange[]> observable
        ) : base(options)
        {
            changeReceiver = new ChangeReceiver(this, observable);
        }

        internal IObservable<IReadOnlyCollection<Change<T>>> Changes<T>() where T : class =>
            changeReceiver.Changes.OfType<Change<T>[]>();
    }
}
