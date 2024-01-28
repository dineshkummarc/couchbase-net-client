#if NETCOREAPP3_1_OR_GREATER
using System;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using Couchbase.Core.Exceptions;
using Couchbase.Stellar.Util;
using Xunit;

namespace Couchbase.UnitTests.Stellar;

public class ClusterTests
{
      #region Stellar

      [Fact]
      public async Task Throw_UnsupportedInProtostellarException_DisposeAsync()
      {
          var cluster = await CreateCluster();
          await Assert.ThrowsAsync<UnsupportedInProtostellarException>(async () => await cluster.DisposeAsync());
      }

      [Fact]
      public async Task Throw_UnsupportedInProtostellarException_Diagnostics()
      {
          var cluster = await CreateCluster();
          await Assert.ThrowsAsync<UnsupportedInProtostellarException>(async () => await cluster.DiagnosticsAsync());
      }

      [Fact]
      public async Task Throw_UnsupportedInProtostellarException_Ping()
      {
          var cluster = await CreateCluster();
          await Assert.ThrowsAsync<UnsupportedInProtostellarException>(async () => await cluster.PingAsync());
      }

      [Fact]
      public async Task Throw_UnsupportedInProtostellarException_Users()
      {
          var cluster = await CreateCluster();
          Assert.Throws<UnsupportedInProtostellarException>(() => cluster.Users);
      }

      [Fact]
      public async Task Throw_UnsupportedInProtostellarException_EventingFunctions()
      {
          var cluster = await CreateCluster();
          Assert.Throws<UnsupportedInProtostellarException>(() => cluster.EventingFunctions);
      }


      [Fact]
      public async Task Throw_UnsupportedInProtostellarException_WaitUntilReadyAsync()
      {
          var cluster = await CreateCluster();
          await Assert.ThrowsAsync<UnsupportedInProtostellarException>(async () => await cluster.WaitUntilReadyAsync(TimeSpan.Zero));
      }

      [Fact]
      public async Task Throw_UnsupportedInProtostellarException_AnalyticsIndexes()
      {
          var cluster = await CreateCluster();
          Assert.Throws<UnsupportedInProtostellarException>(() => cluster.AnalyticsIndexes);
      }

      [Fact]
      public async Task Throw_UnsupportedInProtostellarException_ClusterServices()
      {
          var cluster = await CreateCluster();
          Assert.Throws<UnsupportedInProtostellarException>(() => cluster.ClusterServices);
      }

      [Fact]
      public async Task Throw_AggregateException_On_Cluster_QueryAsync_If_ConnectAsync_Fails()
      {
          var cluster = await CreateCluster();
          await Assert.ThrowsAsync<AggregateException>(async () => await cluster.QueryAsync<dynamic>("SELECT d.* from default2 as d;"));
      }

      [Fact]
      public async Task Throw_AggregateException_On_Cluster_AnalyticsQueryAsync_If_ConnectAsync_Fails()
      {
          var cluster = await CreateCluster();
          await Assert.ThrowsAsync<AggregateException>(async () => await cluster.AnalyticsQueryAsync<dynamic>("SELECT 1;"));
      }

      [Theory]
      [InlineData("couchbase://xxx", typeof(Cluster))]
      [InlineData("couchbases://xxx", typeof(Cluster))]
#if NETCOREAPP3_1_OR_GREATER
      [InlineData("couchbase2://xxx", typeof(Couchbase.Stellar.StellarCluster))]
#endif
      public async Task Test_Schema_Delivers_The_Correct_ICluster_Impl(string connectionString, Type type)
      {
          var options = new ClusterOptions().WithCredentials("Administrator", "password");
          options.KvConnectTimeout = TimeSpan.FromMilliseconds(1);
          var cluster = await Cluster.ConnectAsync(connectionString,options);

          Assert.IsType(type, cluster);
      }

      public async Task<ICluster> CreateCluster()
      {
          var connectionString = "couchbase2://xxx";

          var options = new ClusterOptions().WithCredentials("Administrator", "password");
          options.KvConnectTimeout = TimeSpan.FromMilliseconds(1);
          options.HttpIgnoreRemoteCertificateMismatch = true;
          options.KvIgnoreRemoteCertificateNameMismatch = true;

          return await Cluster.ConnectAsync(connectionString,options);
      }
      #endregion
}
#endif