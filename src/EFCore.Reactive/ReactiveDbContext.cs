using Microsoft.EntityFrameworkCore;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace EFCore.Reactive
{
    public abstract class ReactiveDbContext : DbContext
    {
        private readonly ChangeReceiver changeReceiver;

        public ReactiveDbContext(DbContextOptions options, IObservable<EntityChange[]> observable):base(options) {
            changeReceiver = new ChangeReceiver(this, observable);
        }
        public IObservable<IReadOnlyCollection<Change<T>>> Changes<T>() where T : class =>
             changeReceiver.Changes.OfType<Change<T>[]>();
    }
}
