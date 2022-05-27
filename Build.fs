open Fake.Core
open Fake.IO
open Farmer
open Farmer.Builders

open Helpers

initializeContext()

let sharedPath = Path.getFullName "src/Shared"
let serverPath = Path.getFullName "src/Server"
let clientPath = Path.getFullName "src/Client"
let deployPath = Path.getFullName "deploy"
let sharedTestsPath = Path.getFullName "tests/Shared"
let serverTestsPath = Path.getFullName "tests/Server"
let clientTestsPath = Path.getFullName "tests/Client"

Target.create "Clean" (fun _ ->
    Shell.cleanDir deployPath
    run dotnet "fable clean --yes" clientPath // Delete *.fs.js files created by Fable
)

Target.create "InstallClient" (fun _ -> run npm "install" ".")

Target.create "Bundle" (fun _ ->
    [ "server", dotnet $"publish -c Release -o \"{deployPath}\"" serverPath
      "client", dotnet "fable -o output -s --run webpack -p" clientPath ]
    |> runParallel
)

let docker = createProcess "docker"

let registryName = "safecontainerisedreg"
let regServerKey = "registryLoginServer"
let regUserKey = "registryUsername"
let regPassKey = "registryPassword"
let resourceGroup = "safe-containerised"
let containerEnvironmentName = "safe-containerised-env"
let containerAppName = "safe-containerised-app"
let containerName = "safecontainerised"  // alphanumeric, lowercase only

Target.create "Azure" (fun _ ->

    let tag = System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")

    let registry = containerRegistry {
        name registryName
        sku ContainerRegistry.Basic
        enable_admin_user
    }

    let registryLoginServer, registryUsername, registryPassword =
        let registryDeployment = arm {
            location Location.UKSouth
            add_resources [ registry ]
            output regServerKey registry.LoginServer
            output regUserKey registry.Username
            output regPassKey registry.Password
        }

        let outputs =
            registryDeployment
            |> Deploy.execute resourceGroup [ ]

        outputs.[regServerKey], outputs.[regUserKey], outputs.[regPassKey]

    run docker $"build -t {registryLoginServer}/{containerName}:{tag} ." "."
    run docker $"login {registryLoginServer} -u {registryUsername} -p {registryPassword}" "."
    run docker $"push {registryLoginServer}/{containerName}:{tag}" "."

    let registryCredentials =
        { Server = registryLoginServer
          Username = registryUsername
          Password = SecureParameter "container-registry-pass" }

    let application =
        containerEnvironment {
            name containerEnvironmentName
            add_containers [
                containerApp {
                    name containerAppName
                    add_containers [
                        container {
                            name containerName
                            cpu_cores 0.25<VCores>
                            memory 0.5<Gb>
                            private_docker_image $"{registryName}.azurecr.io" containerName tag
                        }
                    ]
                    add_registry_credentials [ registryCredentials ]
                    replicas 1 5
                    ingress_target_port 80us
                    ingress_transport ContainerApp.Auto
                }
            ]
        }
    
    let deployment = arm {
        location Location.UKSouth
        add_resource application
    }

    deployment
    |> Deploy.execute resourceGroup [ "container-registry-pass", registryPassword ]
    |> ignore
)

Target.create "Run" (fun _ ->
    run dotnet "build" sharedPath
    [ "server", dotnet "watch run" serverPath
      "client", dotnet "fable watch -o output -s --run webpack-dev-server" clientPath ]
    |> runParallel
)

Target.create "RunTests" (fun _ ->
    run dotnet "build" sharedTestsPath
    [ "server", dotnet "watch run" serverTestsPath
      "client", dotnet "fable watch -o output -s --run webpack-dev-server --config ../../webpack.tests.config.js" clientTestsPath ]
    |> runParallel
)

Target.create "Format" (fun _ ->
    run dotnet "fantomas . -r" "src"
)

open Fake.Core.TargetOperators

let dependencies = [
    "Clean"
        ==> "InstallClient"
        ==> "Bundle"
        ==> "Azure"

    "Clean"
        ==> "InstallClient"
        ==> "Run"

    "InstallClient"
        ==> "RunTests"
]

[<EntryPoint>]
let main args = runOrDefault args