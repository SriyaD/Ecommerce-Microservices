using Catalog.Responses;
using MediatR;

namespace Catalog.Queries
{
    // 1. The Query (the request)
    //public record GetAllTypesQuery() : IRequest<IList<TypesResponse>>;
    //                                          ↑
    //                                   declares its own return type

    // 2. The Handler (processes the request)
    //public class GetAllTypesHandler : IRequestHandler<GetAllTypesQuery, IList<TypesResponse>>
    //                                                ↑                 ↑
    //                                           must match         must match
    //                                           query type         query's return type

    // 3. The Controller (dispatches the request)
    //var result = await _mediator.Send(new GetAllTypesQuery());
//                                    ↑
//                    MediatR finds GetAllTypesHandler automatically
    public record GetAllTypesQuery : IRequest<IList<TypesResponse>>
    {
    }
}