using EFCore.Reactive;
using Microsoft.Extensions.DependencyInjection;
using System.Reactive;
using System.Reactive.Subjects;

namespace Microsoft.EntityFrameworkCore
{
    public static class ReactiveDbContextOptionsExtensions
    {
        public static IServiceCollection AddReactiveDbContext<TReactiveDbContext>(
            this IServiceCollection services,
            ISubject<EntityChange[]> subject,
            Action<DbContextOptionsBuilder>? optionsAction = null,
            ServiceLifetime contextLifetime = ServiceLifetime.Scoped,
            ServiceLifetime optionsLifetime = ServiceLifetime.Scoped
        ) where TReactiveDbContext : ReactiveDbContext =>
            AddReactiveDbContext<TReactiveDbContext>(
                services,
                subject,
                optionsAction,
                contextLifetime,
                optionsLifetime
            );

        public static IServiceCollection AddReactiveDbContext<TReactiveDbContext>(
            this IServiceCollection services,
            IObserver<EntityChange[]> observer,
            IObservable<EntityChange[]> observable,
            Action<DbContextOptionsBuilder>? optionsAction = null,
            ServiceLifetime contextLifetime = ServiceLifetime.Scoped,
            ServiceLifetime optionsLifetime = ServiceLifetime.Scoped
        ) where TReactiveDbContext : ReactiveDbContext
        {
            services.Add(new ServiceDescriptor(typeof(IObservable<EntityChange[]>), observable));
            return services.AddDbContext<TReactiveDbContext>(
                options =>
                {
                    optionsAction?.Invoke(options);
                    options.AddInterceptors(new ObserverSaveChangesInterceptor(observer));
                },
                contextLifetime,
                optionsLifetime
            );
        }
    }
}
