using FluentValidation;
using MediatR;
using Ordering.Behaviour;
//Before any MediatR command/query reaches its handler, automatically run all FluentValidation validators.
//If validation fails, throw an exception. If validation succeeds, continue to the handler.

//ValidationBehaviour<TRequest, TResponse> is a MediatR pipeline behavior that provides centralized FluentValidation.
//Before a command or query reaches its handler, it retrieves all validators registered for that request, executes them asynchronously,
//combines their validation failures, and throws a ValidationException if any rules fail.
//If validation succeeds, next() continues the MediatR pipeline and eventually executes the request handler.

//TRequest - Represents whatever command/query is currently being processed.
//IPipelineBehavior<TRequest, TResponse> - comes from MediatR - pipeline behavior allows you to exec sth before and/or after actual request handler.
namespace Ordering.Behaviour
{
    public class ValidationBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
    {
        private readonly IEnumerable<IValidator<TRequest>> _validators;

        public ValidationBehaviour(IEnumerable<IValidator<TRequest>> validators)
        {
            _validators = validators;
        }
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (_validators.Any())
            {
                var context = new ValidationContext<TRequest>(request);
                //Run all the validations one by one and return the Validation Result
                var validationResults = await Task.WhenAll(_validators.Select(v => v.ValidateAsync(context, cancellationToken)));
                //now need to check for any failures
                var failures = validationResults.SelectMany(e => e.Errors).Where(f => f != null).ToList();
                if (failures.Count > 0)
                {
                    throw new ValidationException(failures);
                }
            }
            //On success case
            return await next();
        }
    }
}