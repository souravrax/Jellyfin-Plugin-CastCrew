using System.Reflection;
using Jellyfin.Plugin.CastCrew.Api;
using Jellyfin.Plugin.CastCrew.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
#if NET9_0_OR_GREATER
using User = Jellyfin.Database.Implementations.Entities.User;
#else
using User = Jellyfin.Data.Entities.User;
#endif

namespace Jellyfin.Plugin.CastCrew.Tests.Services;

public class CastCrewPeopleQueryCompatibilityTests
{
    [Theory]
    [InlineData("Actor", null)]
    [InlineData("Director", null)]
    [InlineData("Producer", null)]
    [InlineData("Actor", "Ada")]
    public void Queries_ReturnPeople_FromEachServerApi(string role, string? search)
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Ada", ForcedSortName = "Ada", Overview = "A performer" };
        var library = DispatchProxy.Create<ILibraryManager, ApiProxy>();
        var queryCount = 0;
        ((ApiProxy)(object)library).Handler = (method, args) =>
        {
#if NET10_0_OR_GREATER
            if (method.Name == "ConfigureUserAccess")
            {
                return null;
            }
#endif
            Assert.Equal("GetPeopleItems", method.Name);
            var query = Assert.IsType<InternalPeopleQuery>(args![0]);
            Assert.Contains(role, query.PersonTypes);
            Assert.Equal(0, query.Limit);
            queryCount++;
#if NET10_0_OR_GREATER
            Assert.False(query.EnableTotalRecordCount);
            Assert.NotNull(query.AccessFilter);
            return new MediaBrowser.Model.Querying.QueryResult<BaseItem> { Items = new BaseItem[] { person } };
#else
            return new List<Person> { person };
#endif
        };
        var dto = DispatchProxy.Create<IDtoService, ApiProxy>();
        ((ApiProxy)(object)dto).Handler = (method, args) =>
        {
            Assert.Equal("GetItemByNameDto", method.Name);
            var item = Assert.IsType<Person>(args![0]);
            return new BaseItemDto { Id = item.Id, Name = item.Name };
        };
        var mapping = new CastCrewLibraryPersonMappingService(library, null!, NullLogger<CastCrewLibraryPersonMappingService>.Instance);
        var service = new CastCrewActorQueryService(library, dto, mapping, NullLogger<CastCrewActorQueryService>.Instance);
        var query = new CastCrewActorsQuery { SearchTerm = search, Limit = 10, SortBy = "Random" };
        var user = new User("test", "test", "test");
        var result = role switch
        {
            "Director" => service.QueryDirectors(query, user),
            "Producer" => service.QueryProducers(query, user),
            _ => service.QueryActors(query, user)
        };
        Assert.Equal(1, result.TotalRecordCount);
        Assert.Equal(person.Id, Assert.Single(result.Items).Id);
        Assert.Equal(search is null ? 1 : 2, queryCount);
    }

    public class ApiProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
