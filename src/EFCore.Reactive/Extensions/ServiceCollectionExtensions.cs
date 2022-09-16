using EFCore.Reactive;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ServiceCollectionExtensions
    {
        static IServiceCollection AddReactiveChangeListener(
            this IServiceCollection services,
            IObservable<EntityChange[]> observable
        )
        {
            var dbContextServiceDescriptor = services.FirstOrDefault(
                s => s.ServiceType.IsAssignableTo(typeof(DbContext))
            );
            if (dbContextServiceDescriptor == null)
                throw new InvalidOperationException(
                    $"DbContext must be registered with service provider prior to calling {nameof(AddReactiveChangeListener)}"
                );

            // Register the change receiver with the same lifetime as the context
            services.Add(
                new ServiceDescriptor(
                    typeof(ChangeReceiver),
                    provider =>
                        new ChangeReceiver(
                            (DbContext)provider.GetRequiredService(dbContextServiceDescriptor.ServiceType),
                            observable
                        ),
                    dbContextServiceDescriptor.Lifetime
                )
            );
            return services;
        }
    }
}
