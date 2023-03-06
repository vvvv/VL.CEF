using System;

namespace VL.CEF
{
    public sealed class QueryHandler
    {
        public string Name { get; }

        private readonly Func<object, object> Handler;

        public QueryHandler(string name, Func<object, object> handler)
        {
            Name = name;
            Handler = handler;
        }

        internal object Invoke(object argument) => Handler(argument);
    }
}
