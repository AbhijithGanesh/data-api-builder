// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Azure.DataApiBuilder.Core.Models
{
    public static class HttpContextExtensions
    {
        /// <summary>
        /// Attempting to get a correlationid from headers first
        /// if user has a correlation id passed with the request,
        /// otherwise getting correlation id from http context items
        /// which is generated by CorrelationIdMiddleware
        /// </summary>
        /// <param name="context">http context for current request</param>
        /// <returns>either a GUID or null is returned</returns>
        public static Guid? GetCorrelationId(this HttpContext context)
        {
            Guid correlationId;
            if (context.Request.Headers.TryGetValue(HttpHeaders.CORRELATION_ID, out StringValues correlationIdFromHeader)
                && Guid.TryParse(correlationIdFromHeader, out correlationId))
            {
                return correlationId;
            }

            if (context.Items.TryGetValue(HttpHeaders.CORRELATION_ID, out object? correlationIdItem)
                && Guid.TryParse(correlationIdItem?.ToString(), out correlationId))
            {
                return correlationId;
            }

            return null;
        }

        /// <summary>
        /// Return a string representing the labeled correlation id for use in logging.
        /// If no correlation id is present we instead return the empty string.
        /// </summary>
        /// <param name="context">HttpContext that holds the correlation id if one exists.</param>
        /// <returns>string representing correlation id.</returns>
        public static string GetLoggerCorrelationId(HttpContext? context)
        {
            Guid? correlationId = context is not null ? GetCorrelationId(context) : null;
            return correlationId is not null ? $"{correlationId}" : "";
        }
    }
}
