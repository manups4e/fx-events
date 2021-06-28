using System;

namespace Moonlight.Events.Exceptions
{
    public class EventTimeoutException : Exception
    {
        public EventTimeoutException(string message) : base(message)
        {
        }

        public EventTimeoutException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}