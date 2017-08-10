﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Connector;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace Microsoft.Bot.Builder
{
    public interface IConnector
    {
        Task Receive(IActivity activity, CancellationToken token);

        Task Post(IList<IActivity> activities, CancellationToken token);
    }

    public interface IHttpConnector : IConnector
    {
        Task Receive(IDictionary<string, StringValues> headers, IActivity activity, CancellationToken token);
    }

    public abstract class Connector : IConnector
    {
        protected readonly IServiceProvider serviceProvider;
        public Connector(IServiceProvider serviceProvider)
        {
            SetField.NotNull(out this.serviceProvider, nameof(serviceProvider), serviceProvider);
        }
        
        public abstract Task Post(IList<IActivity> activities, CancellationToken token);

        public virtual async Task Receive(IActivity activity, CancellationToken token)
        {
            //register current activity with resolver so connectorClient can be instantiated correctly
            this.serviceProvider.GetRequiredService<ActivityResolver>().Register(activity);
            
            //get the context factory and create a new botContext
            var factory = this.serviceProvider.GetRequiredService<IBotContextFactory>();
            var context = await factory.CreateBotContext(activity, token);

            // get bot object from container and post the activity to bot
            var bot = this.serviceProvider.GetRequiredService<Bot>();
            await bot.Receive(context, token);
        }
    }

    public class BotFrameworkConnector : Connector, IHttpConnector
    {
        private readonly BotAuthenticator botAuthenticator;
        
        public BotFrameworkConnector(IServiceProvider serviceProvider, BotAuthenticator botAuthenticator) 
            : base(serviceProvider)
        {
            SetField.NotNull(out this.botAuthenticator, nameof(botAuthenticator), botAuthenticator);
        }

        public async override Task Post(IList<IActivity> activities, CancellationToken token)
        {
            var connectorClient = this.serviceProvider.GetRequiredService<IConnectorClient>();
            foreach (Activity activity in activities)
            {
                await connectorClient.Conversations.SendToConversationAsync(activity, token);
            }
        }

        public async Task Receive(IDictionary<string, StringValues> headers, IActivity activity, CancellationToken token)
        {
            if (await botAuthenticator.TryAuthenticateAsync(headers, new[] { activity }, token))
            {
                await base.Receive(activity, token);
            }
            else
            {
                throw new UnauthorizedAccessException();
            }
        }
    }

    public class TraceConnector : Connector
    {
        public TraceConnector(IServiceProvider serviceProvider) 
            : base(serviceProvider)
        {
        }

        public override Task Post(IList<IActivity> activities, CancellationToken token)
        {
            foreach (var activity in activities)
            {
                if (activity.GetActivityType() == ActivityTypes.Message)
                {
                    Trace.WriteLine((activity as IMessageActivity).Text);
                }
                else
                {
                    Trace.WriteLine((activity as Activity).Type);
                }
            }
            return Task.CompletedTask;
        }
    }

    public class ConsoleConnector : Connector
    {
        public ConsoleConnector(IServiceProvider serviceProvider) : base(serviceProvider)
        {
        }

        public override Task Post(IList<IActivity> activities, CancellationToken token)
        {
            foreach (Activity activity in activities)
            {
                switch (activity.GetActivityType())
                {
                    case ActivityTypes.Message:
                        if (activity.Attachments != null && activity.Attachments.Any())
                        {
                            var attachment = activity.Attachments.Count == 1 ? "1 attachments" : $"{activity.Attachments.Count()} attachments";
                            Console.WriteLine($"{activity.Text} with {attachment} ");
                        }
                        else
                        {
                            Console.WriteLine($"{activity.Text}");
                        }
                        break;
                    default:
                        Console.WriteLine("Bot: acitivyt type: {0}", activity.Type);
                        break;

                }
            }
            return Task.CompletedTask;
        }

        public static async Task Listen(IServiceCollection collection)
        {
            var provider = collection.BuildServiceProvider();
            while (true)
            {
                var msg = Console.ReadLine();
                if (msg == null || msg.ToLower() == "quit")
                {
                    break;
                }

                var activity = new Activity()
                {
                    Text = msg,
                    ChannelId = "console",
                    From = new ChannelAccount(id: "user", name: "User1"),
                    Recipient = new ChannelAccount(id: "bot", name: "Bot"),
                    Conversation = new ConversationAccount(id: "Convo1"),
                    Timestamp = DateTime.UtcNow,
                    Id = Guid.NewGuid().ToString(),
                    Type = ActivityTypes.Message
                };

                using (var scope = provider.CreateScope())
                {
                    var connector = scope.ServiceProvider.GetRequiredService<Builder.ConsoleConnector>();
                    await connector.Receive(activity, CancellationToken.None);
                }
            }
        }
    }

    public static partial class ConnectorExtensions 
    {
        public static IServiceCollection UseTraceConnector(this IServiceCollection services)
        {
            services.AddScoped<IConnector, TraceConnector>();
            return services;
        }

        public static IServiceCollection UseConsoleConnector(this IServiceCollection services)
        {
            services.AddScoped<ConsoleConnector>();
            services.AddScoped<IConnector>(p => p.GetRequiredService<ConsoleConnector>());
            return services;
        }

        public static IServiceCollection UseBotFrameworkConnector(this IServiceCollection services)
        {
            services.UseBotConnector();
            services.AddScoped<IConnector, BotFrameworkConnector>();
            services.AddScoped<IHttpConnector, BotFrameworkConnector>();
            services.AddScoped<IConnectorClient>(provider =>
            {
                var activity = provider.GetRequiredService<IActivity>();
                return new ConnectorClient(new Uri(activity.ServiceUrl));
            });
            services.AddSingleton<BotAuthenticator>(provider =>
            {
                var config = provider.GetRequiredService<IConfigurationRoot>();
                var appId = config.GetSection(MicrosoftAppCredentials.MicrosoftAppIdKey)?.Value;
                var passwrod = config.GetSection(MicrosoftAppCredentials.MicrosoftAppPasswordKey)?.Value;
                return new BotAuthenticator(appId, passwrod);
            });
            return services;
        }

    }
}
