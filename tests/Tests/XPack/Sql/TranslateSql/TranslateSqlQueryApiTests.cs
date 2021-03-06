// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using Elastic.Elasticsearch.Xunit.XunitPlumbing;
using Elasticsearch.Net;
using FluentAssertions;
using Nest;
using Tests.Core.ManagedElasticsearch.Clusters;
using Tests.Domain;
using Tests.Domain.Helpers;
using Tests.Framework.EndpointTests;
using Tests.Framework.EndpointTests.TestState;

namespace Tests.XPack.Sql.TranslateSql
{
	//[SkipVersion("<6.4.0", "")]
	[SkipVersion(">=8.0.0-SNAPSHOT", "TODO investigate")]
	public class TranslateSqlApiTests
		: ApiIntegrationTestBase<XPackCluster, TranslateSqlResponse, ITranslateSqlRequest, TranslateSqlDescriptor, TranslateSqlRequest>
	{
		private static readonly string SqlQuery =
			$@"SELECT type, name, startedOn, numberOfCommits
FROM {TestValueHelper.ProjectsIndex}
WHERE type = '{Project.TypeName}'
ORDER BY numberOfContributors DESC";

		public TranslateSqlApiTests(XPackCluster cluster, EndpointUsage usage) : base(cluster, usage) { }

		protected override bool ExpectIsValid => true;

		protected override object ExpectJson { get; } = new
		{
			query = SqlQuery,
			fetch_size = 5
		};

		protected override int ExpectStatusCode => 200;

		protected override Func<TranslateSqlDescriptor, ITranslateSqlRequest> Fluent => d => d
			.Query(SqlQuery)
			.FetchSize(5);

		protected override HttpMethod HttpMethod => HttpMethod.POST;

		protected override TranslateSqlRequest Initializer => new TranslateSqlRequest()
		{
			Query = SqlQuery,
			FetchSize = 5
		};

		protected override string UrlPath => $"/_sql/translate";

		protected override LazyResponses ClientUsage() => Calls(
			(client, f) => client.Sql.Translate(f),
			(client, f) => client.Sql.TranslateAsync(f),
			(client, r) => client.Sql.Translate(r),
			(client, r) => client.Sql.TranslateAsync(r)
		);

		protected override void ExpectResponse(TranslateSqlResponse response)
		{
			var search = response.Result;
			search.Should().NotBeNull();
			search.Size.Should().HaveValue().And.Be(5);
			search.Source.Should().NotBeNull();
			search.Source.Match(b => b.Should().BeFalse(), f => f.Should().BeNull());
			// TODO DocValueFields is gone after code gen rework on 7.x
			// We used to generate these documented in the spec as params to be implemented on the body
//			search.DocValueFields.Should()
//				.NotBeNullOrEmpty()
//				.And.HaveCount(4)
//				.And.Contain("type")
//				.And.Contain("name")
//				.And.Contain("startedOn")
//				.And.Contain("numberOfCommits");

			search.Query.Should().NotBeNull();
			IQueryContainer q = search.Query;
			q.Term.Should().NotBeNull();
			q.Term.Value.Should().Be(TestValueHelper.ProjectsIndex);
			q.Term.Field.Should().Be("type");
		}
	}
}
