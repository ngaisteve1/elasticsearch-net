module Tests.YamlRunner.TestSuiteBootstrap

open System
open System.Linq

open Elasticsearch.Net
open Elasticsearch.Net.Specification.CatApi
open Elasticsearch.Net.Specification.ClusterApi
open Elasticsearch.Net.Specification.IndicesApi
open Tests.YamlRunner.Models

let DefaultSetup : Operation list = [Actions("Setup", fun (client, suite) ->
    let firstFailure (responses:DynamicResponse seq) =
            responses
            |> Seq.filter (fun r -> not r.Success && r.HttpStatusCode <> Nullable.op_Implicit 404)
            |> Seq.tryHead
    
    match suite with
    | Oss ->
        let deleteAll = client.Indices.Delete<DynamicResponse>("*")
        let templates =
            client.Cat.Templates<StringResponse>("*", CatTemplatesRequestParameters(Headers=["name"].ToArray()))
                .Body.Split("\n")
                |> Seq.filter(fun f -> not(String.IsNullOrWhiteSpace(f)) && not(f.StartsWith(".")) && f <> "security-audit-log")
                //TODO template does not accept comma separated list but is documented as such
                |> Seq.map(fun template -> client.Indices.DeleteTemplateForAll<DynamicResponse>(template))
                |> Seq.toList
        firstFailure <| [deleteAll] @ templates
        
    | XPack ->
        firstFailure <| seq {
            //delete all templates
            let templates =
                client.Cat.Templates<StringResponse>("*", CatTemplatesRequestParameters(Headers=["name"].ToArray()))
                    .Body.Split("\n")
                    |> Seq.filter(fun f -> not(String.IsNullOrWhiteSpace(f)) && not(f.StartsWith(".")) && f <> "security-audit-log")
                    //TODO template does not accept comma separated list but is documented as such
                    |> Seq.map(fun template -> client.Indices.DeleteTemplateForAll<DynamicResponse>(template))
            
            yield! templates
            
            yield client.Watcher.Delete<DynamicResponse>("my_watch")
            
            let deleteNonReserved (setup:_ -> DynamicResponse) (delete:(_ -> DynamicResponse)) = 
                setup().Dictionary.GetKeyValues()
                |> Seq.map (fun kv ->
                    match kv.Value.Get<bool> "metadata._reserved" with
                    | false -> Some <| delete(kv.Key)
                    | _ -> None
                )
                |> Seq.choose id
                |> Seq.toList
            
            yield! //roles
                deleteNonReserved
                   (fun _ -> client.Security.GetRole<DynamicResponse>())
                   (fun role -> client.Security.DeleteRole<DynamicResponse> role)
                   
            yield! //users
                deleteNonReserved
                   (fun _ -> client.Security.GetUser<DynamicResponse>())
                   (fun user -> client.Security.DeleteUser<DynamicResponse> user)
            
            yield! //privileges
                deleteNonReserved
                   (fun _ -> client.Security.GetPrivileges<DynamicResponse>())
                   (fun priv -> client.Security.DeletePrivileges<DynamicResponse>(priv, "_all"))
                
            // deleting feeds before jobs is important
            let mlDataFeeds = 
                let stopFeeds = client.MachineLearning.StopDatafeed<DynamicResponse>("_all")
                let getFeeds = client.MachineLearning.GetDatafeeds<DynamicResponse> ()
                let deleteFeeds =
                    getFeeds.Get<string[]> "datafeeds.datafeed_id"
                    |> Seq.map (fun jobId -> client.MachineLearning.DeleteDatafeed<DynamicResponse>(jobId))
                    |> Seq.toList
                [stopFeeds; getFeeds] @ deleteFeeds
            yield! mlDataFeeds
                
            yield client.IndexLifecycleManagement.RemovePolicy<DynamicResponse>("_all")
            
            let mlJobs = 
                let closeJobs = client.MachineLearning.CloseJob<DynamicResponse>("_all", PostData.Empty)
                let getJobs = client.MachineLearning.GetJobs<DynamicResponse> "_all"
                let deleteJobs =
                    getJobs.Get<string[]> "jobs.job_id"
                    |> Seq.map (fun jobId -> client.MachineLearning.DeleteJob<DynamicResponse>(jobId))
                    |> Seq.toList
                [closeJobs; getJobs] @ deleteJobs
            yield! mlJobs
                
            let rollupJobs = 
                let getJobs = client.Rollup.GetJob<DynamicResponse> "_all"
                let deleteJobs =
                    getJobs.Get<string[]> "jobs.config.id"
                    |> Seq.collect (fun jobId -> [
                         client.Rollup.StopJob<DynamicResponse>(jobId)
                         client.Rollup.DeleteJob<DynamicResponse>(jobId)
                    ])
                    |> Seq.toList
                [getJobs] @ deleteJobs
            yield! rollupJobs
                
            let tasks =
                let getJobs = client.Tasks.List<DynamicResponse> ()
                let cancelJobs = 
                    let dict = getJobs.Get<DynamicDictionary> "nodes"
                    dict.GetKeyValues()
                    |> Seq.collect(fun kv ->
                        let dict = kv.Value.Get<DynamicDictionary> "tasks"
                        dict.GetKeyValues()
                    )
                    |> Seq.map (fun kv ->
                        match kv.Value.Get<bool> "cancellable" with
                        | true -> Some <| client.Tasks.Cancel<DynamicResponse>(kv.Key)
                        | _ -> None
                    )
                    |> Seq.choose id
                    |> Seq.toList
                
                [getJobs] @ cancelJobs
            yield! tasks
            
            let transforms =
                let transforms = client.Transform.Get<DynamicResponse> "_all"
                let stopTransforms =
                    transforms.Get<string[]> "transforms.id"
                    |> Seq.collect (fun id -> [
                         client.Transform.Stop<DynamicResponse> id
                         client.Transform.Delete<DynamicResponse> id
                    ])
                    |> Seq.toList
                [transforms] @ stopTransforms
            yield! transforms
                
            let yellowStatus = Nullable.op_Implicit WaitForStatus.Yellow
            yield client.Cluster.Health<DynamicResponse>(ClusterHealthRequestParameters(WaitForStatus=yellowStatus))
            
            let indices =
                let dp = DeleteIndexRequestParameters()
                dp.SetQueryString("expand_wildcards", "open,closed,hidden")
                client.Indices.Delete<DynamicResponse>("*", dp)
            yield indices
            
            let data = PostData.String @"{""password"":""x-pack-test-password"", ""roles"":[""superuser""]}"
            yield client.Security.PutUser<DynamicResponse>("x_pack_rest_user", data)
            
            let refreshAll =
                let rp = RefreshRequestParameters()
                rp.SetQueryString("expand_wildcards", "open,closed,hidden")
                client.Indices.Refresh<DynamicResponse>( "_all", rp)
                
            yield refreshAll
            
            yield client.Cluster.Health<DynamicResponse>(ClusterHealthRequestParameters(WaitForStatus=yellowStatus))
        }
)]
