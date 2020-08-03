namespace FSharp.Formatting.CommandTool

open CommandLine

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.IO
open System.Globalization
open System.Text
open System.Runtime.Serialization.Formatters.Binary

open FSharp.Formatting.Common
open FSharp.Formatting.HtmlModel
open FSharp.Formatting.HtmlModel.Html
open FSharp.Formatting.Literate
open FSharp.Formatting.ApiDocs
open FSharp.Formatting.Literate.Evaluation
open FSharp.Formatting.CommandTool.Common
open FSharp.Formatting.Templating

open Dotnet.ProjInfo
open Dotnet.ProjInfo.Workspace

open Suave
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Suave.Operators
open Suave.Filters

module Utils =
    let saveBinary (object:'T) (fileName:string) =
        try Directory.CreateDirectory (Path.GetDirectoryName(fileName)) |> ignore with _ -> ()
        let formatter = BinaryFormatter()
        use fs = new FileStream(fileName, FileMode.Create)
        formatter.Serialize(fs, object)
        fs.Flush()

    let loadBinary<'T> (fileName:string):'T option =
        let formatter = BinaryFormatter()
        use fs = new FileStream(fileName, FileMode.Open)
        try
            let object = formatter.Deserialize(fs) :?> 'T
            Some object
        with e -> None

    let cacheBinary cacheFile cacheValid (f: unit -> 'T)  : 'T =
        let attempt =
            if File.Exists(cacheFile) then
               let v = loadBinary cacheFile
               match v with
               | Some v ->
                   if cacheValid v then
                       printfn "restored project state from '%s'" cacheFile
                       Some v
                   else
                       printfn "discarding project state in '%s' as now invalid" cacheFile
                       None
               | None -> None
            else None
        match attempt with
        | Some r -> r
        | None ->
            let res = f()
            saveBinary res cacheFile
            res

module Crack =

    let msbuildPropBool (s: string) =
        match s.Trim() with
        | "" -> None
        | Inspect.MSBuild.ConditionEquals "True" -> Some true
        | _ -> Some false

    let runProcess (log: string -> unit) (workingDir: string) (exePath: string) (args: string) =
        let psi = System.Diagnostics.ProcessStartInfo()
        psi.FileName <- exePath
        psi.WorkingDirectory <- workingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.Arguments <- args
        psi.CreateNoWindow <- true
        psi.UseShellExecute <- false

        use p = new System.Diagnostics.Process()
        p.StartInfo <- psi

        p.OutputDataReceived.Add(fun ea -> log (ea.Data))

        p.ErrorDataReceived.Add(fun ea -> log (ea.Data))

        // printfn "running: %s %s" psi.FileName psi.Arguments

        p.Start() |> ignore
        p.BeginOutputReadLine()
        p.BeginErrorReadLine()
        p.WaitForExit()

        let exitCode = p.ExitCode

        exitCode, (workingDir, exePath, args)

    type CrackedProjectInfo =
        { ProjectFileName : string
          TargetPath : string option
          IsTestProject : bool
          IsLibrary : bool
          IsPackable : bool
          FsDocsSourceFolder : string option
          FsDocsSourceRepository : string option
          RepositoryUrl : string option
          RepositoryType : string option
          RepositoryBranch : string option
          UsesMarkdownComments: bool
          FsDocsCollectionNameLink : string option
          FsDocsLicenseLink : string option
          FsDocsLogoLink : string option
          FsDocsLogoSource : string option
          PackageProjectUrl : string option
          Authors : string option
          GenerateDocumentationFile : bool
          //Removed because this is typically a multi-line string and dotnet-proj-info can't handle this
          //Description : string option
          PackageLicenseExpression : string option
          PackageTags : string option
          Copyright : string option
          PackageVersion : string option
          PackageIconUrl : string option
          //Removed because this is typically a multi-line string and dotnet-proj-info can't handle this
          //PackageReleaseNotes : string option
          RepositoryCommit : string option }

    let crackProjectFile slnDir (file : string) : CrackedProjectInfo =

        let projDir = Path.GetDirectoryName(file)
        let projectAssetsJsonPath = Path.Combine(projDir, "obj", "project.assets.json")
        if not(File.Exists(projectAssetsJsonPath)) then
            failwithf "project '%s' not restored" file

        let additionalInfo =
            [ "OutputType"
              "IsTestProject"
              "IsPackable"
              "RepositoryUrl"
              "UsesMarkdownComments"
              "FsDocsCollectionNameLink"
              "FsDocsLogoSource"
              "FsDocsLogoLink"
              "FsDocsLicenseLink"
              "FsDocsSourceFolder"
              "FsDocsSourceRepository"
              "RepositoryType"
              "RepositoryBranch"
              "PackageProjectUrl"
              "Authors"
              "GenerateDocumentationFile"
              //Removed because this is typically a multi-line string and dotnet-proj-info can't handle this
              //"Description"
              "PackageLicenseExpression"
              "PackageTags"
              "Copyright"
              "PackageVersion"
              "PackageIconUrl"
              //Removed because this is typically a multi-line string and dotnet-proj-info can't handle this
              //"PackageReleaseNotes"
              "RepositoryCommit"
            ]
        let gp () = Inspect.getProperties (["TargetPath"] @ additionalInfo)

        let loggedMessages = System.Collections.Concurrent.ConcurrentQueue<string>()
        let runCmd exePath args =
           let args = List.append args [ "/p:DesignTimeBuild=true" ]
           //printfn "%s, args = %A" exePath args
           let res = runProcess loggedMessages.Enqueue slnDir exePath (args |> String.concat " ")
           //printfn "done..."
           res

        let msbuildPath = Inspect.MSBuildExePath.DotnetMsbuild "dotnet"
        let msbuildExec = Inspect.msbuild msbuildPath runCmd

        let result = file |> Inspect.getProjectInfos loggedMessages.Enqueue msbuildExec [gp] []

        let msgs = (loggedMessages.ToArray() |> Array.toList)
        //printfn "msgs = %A" msgs
        match result with
        | Ok [gpResult] ->
            match gpResult with
            | Ok (Inspect.GetResult.Properties props) ->
                let props = props |> Map.ofList
                let msbuildPropString prop = props |> Map.tryFind prop |> Option.bind (function s when String.IsNullOrWhiteSpace(s) -> None | s -> Some s)
                let msbuildPropBool prop = prop |> msbuildPropString |> Option.bind msbuildPropBool

                {  ProjectFileName = file
                   TargetPath = msbuildPropString "TargetPath"
                   IsTestProject = msbuildPropBool "IsTestProject" |> Option.defaultValue false
                   IsLibrary = msbuildPropString "OutputType" |> Option.map (fun s -> s.ToLowerInvariant()) |> ((=) (Some "library"))
                   IsPackable = msbuildPropBool "IsPackable" |> Option.defaultValue false
                   RepositoryUrl = msbuildPropString "RepositoryUrl" 
                   RepositoryType = msbuildPropString "RepositoryType" 
                   RepositoryBranch = msbuildPropString "RepositoryBranch" 
                   FsDocsCollectionNameLink = msbuildPropString "FsDocsCollectionNameLink" 
                   FsDocsSourceFolder = msbuildPropString "FsDocsSourceFolder" 
                   FsDocsSourceRepository = msbuildPropString "FsDocsSourceRepository" 
                   FsDocsLicenseLink = msbuildPropString "FsDocsLicenseLink" 
                   FsDocsLogoLink = msbuildPropString "FsDocsLogoLink" 
                   FsDocsLogoSource = msbuildPropString "FsDocsLogoSource" 
                   UsesMarkdownComments = msbuildPropBool "UsesMarkdownComments" |> Option.defaultValue false
                   PackageProjectUrl = msbuildPropString "PackageProjectUrl" 
                   Authors = msbuildPropString "Authors" 
                   GenerateDocumentationFile = msbuildPropBool "GenerateDocumentationFile" |> Option.defaultValue false
                   PackageLicenseExpression = msbuildPropString "PackageLicenseExpression" 
                   PackageTags = msbuildPropString "PackageTags" 
                   Copyright = msbuildPropString "Copyright"
                   PackageVersion = msbuildPropString "PackageVersion"
                   PackageIconUrl = msbuildPropString "PackageIconUrl"
                   RepositoryCommit = msbuildPropString "RepositoryCommit" }
                
            | Ok ok -> failwithf "huh? ok = %A" ok
            | Error err -> failwithf "error - %s\nlog - %s" (err.ToString()) (String.concat "\n" msgs)
        | Ok ok -> failwithf "huh? ok = %A" ok
        | Error err -> failwithf "error - %s\nlog - %s" (err.ToString()) (String.concat "\n" msgs)
                
    let getProjectsFromSlnFile (slnPath : string) =
        match InspectSln.tryParseSln slnPath with
        | Ok (_, slnData) ->
            InspectSln.loadingBuildOrder slnData

            //this.LoadProjects(projs, crosstargetingStrategy, useBinaryLogger, numberOfThreads)
        | Error d ->
            failwithf "cannot load the sln: %A" d

    let crackProjects (userRoot, userCollectionName, userParameters, projects) =
        let slnDir = Path.GetFullPath "."
        
        //printfn "x.projects = %A" x.projects
        let collectionName, projectFiles =
            match projects with
            | [] ->
                match Directory.GetFiles(slnDir, "*.sln") with
                | [| sln |] ->
                    printfn "getting projects from solution file %s" sln
                    let collectionName = defaultArg userCollectionName (Path.GetFileNameWithoutExtension(sln))
                    collectionName, getProjectsFromSlnFile sln
                | _ -> 
                    let projectFiles =
                        [ yield! Directory.EnumerateFiles(slnDir, "*.fsproj")
                          for d in Directory.EnumerateDirectories(slnDir) do
                             yield! Directory.EnumerateFiles(d, "*.fsproj")
                             for d2 in Directory.EnumerateDirectories(d) do
                                yield! Directory.EnumerateFiles(d2, "*.fsproj") ]

                    let collectionName = 
                        defaultArg userCollectionName 
                           (match projectFiles with
                            | [ file1 ] -> Path.GetFileNameWithoutExtension(file1)
                            | _ -> Path.GetFileName(slnDir))

                    collectionName, projectFiles
                    
            | projectFiles -> 
                let collectionName = Path.GetFileName(slnDir)
                collectionName, projectFiles
    
          //printfn "projects = %A" projectFiles
        let projectFiles =
            projectFiles |> List.choose (fun s ->
                if s.Contains(".Tests") || s.Contains("test") then
                    printfn "skipping project '%s' because it looks like a test project" (Path.GetFileName s) 
                    None
                else
                    Some s)
        printfn "filtered projects = %A" projectFiles
        if projectFiles.Length = 0 then
            printfn "no project files found, no API docs will be generated"

        printfn "cracking projects..." 
        let projectInfos =
            projectFiles
            |> Array.ofList
            |> Array.Parallel.choose (fun p -> 
                try
                   Some (crackProjectFile slnDir p)
                with e -> 
                   printfn "skipping project '%s' because an error occurred while cracking it: %A" (Path.GetFileName p) e
                   None)
            |> Array.toList

        printfn "projectInfos = %A" projectInfos
        let projectInfos =
              projectInfos
              |> List.choose (fun info ->
                let shortName = Path.GetFileName info.ProjectFileName
                if info.TargetPath.IsNone then
                    printfn "skipping project '%s' because it doesn't have a target path" shortName
                    None
                elif not info.IsLibrary then 
                    printfn "skipping project '%s' because it isn't a library" shortName
                    None
                elif info.IsTestProject then 
                    printfn "skipping project '%s' because it has <IsTestProject> true" shortName
                    None
                elif not info.GenerateDocumentationFile then 
                    printfn "skipping project '%s' because it doesn't have <GenerateDocumentationFile>" shortName
                    None
                else
                    Some info)

        printfn "projectInfos = %A" projectInfos

        if projectInfos.Length = 0 && projectFiles.Length > 0 then
            printfn "Error while cracking project files, no project files succeeded, exiting."
            exit 1

        let param (ParamKey tag as key) v =
            match v with
            | Some v -> (key, v)
            | None -> (key, "{{" + tag + "}}")

        // For the 'docs' directory we use the best info we can find from across all projects
        let projectInfoForDocs =
              {
                  ProjectFileName = ""
                  TargetPath = None
                  IsTestProject = false
                  IsLibrary = true
                  IsPackable = true
                  RepositoryUrl =  projectInfos |> List.tryPick  (fun info -> info.RepositoryUrl)  
                  RepositoryType =  projectInfos |> List.tryPick  (fun info -> info.RepositoryType)
                  RepositoryBranch = projectInfos |> List.tryPick  (fun info -> info.RepositoryBranch)
                  FsDocsCollectionNameLink = projectInfos |> List.tryPick  (fun info -> info.FsDocsCollectionNameLink)
                  FsDocsLicenseLink = projectInfos |> List.tryPick  (fun info -> info.FsDocsLicenseLink)
                  FsDocsLogoLink = projectInfos |> List.tryPick  (fun info -> info.FsDocsLogoLink)
                  FsDocsLogoSource = projectInfos |> List.tryPick  (fun info -> info.FsDocsLogoSource)
                  FsDocsSourceFolder = projectInfos |> List.tryPick  (fun info -> info.FsDocsSourceFolder)
                  FsDocsSourceRepository = projectInfos |> List.tryPick  (fun info -> info.FsDocsSourceRepository)
                  PackageProjectUrl = projectInfos |> List.tryPick  (fun info -> info.PackageProjectUrl)
                  Authors = projectInfos |> List.tryPick  (fun info -> info.Authors)
                  GenerateDocumentationFile = true
                  PackageLicenseExpression = projectInfos |> List.tryPick  (fun info -> info.PackageLicenseExpression)
                  PackageTags = projectInfos |> List.tryPick  (fun info -> info.PackageTags)
                  UsesMarkdownComments = false
                  Copyright = projectInfos |> List.tryPick  (fun info -> info.Copyright)
                  PackageVersion = projectInfos |> List.tryPick  (fun info -> info.PackageVersion)
                  PackageIconUrl = projectInfos |> List.tryPick  (fun info -> info.PackageIconUrl)
                  RepositoryCommit = projectInfos |> List.tryPick  (fun info -> info.RepositoryCommit)
              }

        let parametersForProjectInfo (info: CrackedProjectInfo) =
              userParameters @
              [ param ParamKey.``root`` (Some userRoot)
                param ParamKey.``fsdocs-authors`` info.Authors
                param ParamKey.``fsdocs-collection-name`` (Some collectionName)
                param ParamKey.``fsdocs-collection-name-link`` (Some (defaultArg info.FsDocsCollectionNameLink userRoot))
                param ParamKey.``fsdocs-copyright`` info.Copyright
                param ParamKey.``fsdocs-logo-src`` (Some (defaultArg info.FsDocsLogoSource (sprintf "%simg/logo.png" userRoot)))
                param ParamKey.``fsdocs-logo-link`` (Some (defaultArg info.FsDocsLogoLink userRoot))
                param ParamKey.``fsdocs-license-link`` (Some (defaultArg info.FsDocsLicenseLink (sprintf "%simg/blob/master/LICENSE.md" userRoot)))
                param ParamKey.``fsdocs-package-project-url`` info.PackageProjectUrl
                param ParamKey.``fsdocs-package-license-expression`` info.PackageLicenseExpression
                param ParamKey.``fsdocs-package-icon-url`` info.PackageIconUrl
                param ParamKey.``fsdocs-package-tags`` info.PackageTags
                param ParamKey.``fsdocs-package-version`` info.PackageVersion
                param ParamKey.``fsdocs-repository-link`` info.RepositoryUrl
                param ParamKey.``fsdocs-repository-branch`` info.RepositoryBranch
                param ParamKey.``fsdocs-repository-type`` info.RepositoryType
                param ParamKey.``fsdocs-repository-commit`` info.RepositoryCommit
              ]

        let projects =  
            [ for info in projectInfos do
                 let parameters = parametersForProjectInfo info
                 (info.TargetPath.Value, info.RepositoryUrl, info.RepositoryBranch, info.RepositoryType,
                  info.UsesMarkdownComments, info.FsDocsSourceFolder, info.FsDocsSourceRepository, parameters) ]
              
        let paths = [ for info in projectInfos -> Path.GetDirectoryName info.TargetPath.Value ]

        let docsParameters = parametersForProjectInfo projectInfoForDocs

        collectionName, projects, paths, docsParameters

/// Convert markdown, script and other content into a static site
type internal DocContent(outputDirectory, previous: Map<_,_>, lineNumbers, fsiEvaluator, parameters, saveImages) =

  let ensureDirectory path =
    let dir = DirectoryInfo(path)
    if not dir.Exists then dir.Create()

  let createImageSaver (outputDirectory) =
        // Download images so that they can be embedded
        let wc = new System.Net.WebClient()
        let mutable counter = 0
        fun (url:string) ->
            if url.StartsWith("http") || url.StartsWith("https") then
                counter <- counter + 1
                let ext = Path.GetExtension(url)
                let url2 = sprintf "savedimages/saved%d%s" counter ext
                let fn = sprintf "%s/%s" outputDirectory url2

                ensureDirectory (sprintf "%s/savedimages" outputDirectory)
                printfn "downloading %s --> %s" url fn
                wc.DownloadFile(url, fn)
                url2
            else url

  let processFile (inputFile: string) outputKind template outputPrefix imageSaver =
        [
          let name = Path.GetFileName(inputFile)
          if name.StartsWith(".") then 
              printfn "skipping file %s" inputFile
          elif name.StartsWith "_template" then 
              ()
          else
              let isFsx = inputFile.EndsWith(".fsx", true, CultureInfo.InvariantCulture) 
              let isMd = inputFile.EndsWith(".md", true, CultureInfo.InvariantCulture)

              // A _template.tex or _template.pynb is needed to generate those files
              match outputKind, template with
              | OutputKind.Pynb, None -> ()
              | OutputKind.Latex, None -> ()
              | OutputKind.Fsx, None -> ()
              | _ ->

              let imageSaverOpt = 
                match outputKind with
                | OutputKind.Pynb when saveImages <> Some false -> Some imageSaver
                | OutputKind.Latex when saveImages <> Some false -> Some imageSaver
                | OutputKind.Fsx when saveImages = Some true -> Some imageSaver
                | OutputKind.Html when saveImages = Some true -> Some imageSaver
                | _ -> None

              let ext = outputKind.Extension
              let relativeOutputFile =
                  if isFsx || isMd then
                      let basename = Path.GetFileNameWithoutExtension(inputFile)
                      Path.Combine(outputPrefix, sprintf "%s.%s" basename ext)
                  else
                      Path.Combine(outputPrefix, name)

              // Update only when needed - template or file has changed
              let fileChangeTime = File.GetLastWriteTime(inputFile)
              let templateChangeTime = match template with None -> new DateTime(1,1,1) | Some t -> File.GetLastWriteTime(t)
              let changeTime = max fileChangeTime templateChangeTime
              let generateTime = File.GetLastWriteTime(relativeOutputFile)
              if changeTime > generateTime then
                  if isFsx then
                      printfn "preparing %s --> %s" inputFile relativeOutputFile
                      let inputFile = Path.GetFullPath(inputFile)
                      let model =
                        Literate.ParseAndTransformScriptFile
                          (inputFile, output = relativeOutputFile, outputKind = outputKind,
                            ?formatAgent = None, ?prefix = None, ?fscoptions = None,
                            ?lineNumbers = lineNumbers, references=false, ?fsiEvaluator = fsiEvaluator,
                            parameters = parameters,
                            generateAnchors = true,
                            //?customizeDocument = customizeDocument,
                            //?tokenKindToCss = tokenKindToCss,
                            ?imageSaver=imageSaverOpt)

                      yield (Some (inputFile, model),
                              (fun p ->
                                 let outputFile = Path.GetFullPath(Path.Combine(outputDirectory, outputPrefix))
                                 printfn "writing %s --> %s" inputFile outputFile
                                 SimpleTemplating.UseFileAsSimpleTemplate( p@model.Parameters, template, relativeOutputFile)))

                  elif isMd then
                      printfn "preparing %s --> %s" inputFile relativeOutputFile
                      let inputFile = Path.GetFullPath(inputFile)
                      let model =
                        Literate.ParseAndTransformMarkdownFile
                          (inputFile, output = relativeOutputFile, outputKind = outputKind,
                            ?formatAgent = None, ?prefix = None, ?fscoptions = None,
                            ?lineNumbers = lineNumbers, references=false,
                            parameters = parameters,
                            generateAnchors = true,
                            //?customizeDocument=customizeDocument,
                            //?tokenKindToCss = tokenKindToCss,
                            ?imageSaver=imageSaverOpt)

                      yield (Some (inputFile, model),
                              (fun p ->
                                 let outputFile = Path.GetFullPath(Path.Combine(outputDirectory, outputPrefix))
                                 printfn "writing %s --> %s" inputFile outputFile
                                 SimpleTemplating.UseFileAsSimpleTemplate( p@model.Parameters, template, relativeOutputFile)))

                  else 
                      yield (None, 
                              (fun _p ->
                                  let outputFile = Path.GetFullPath(Path.Combine(outputDirectory, outputPrefix))
                                  printfn "copying %s --> %s" inputFile relativeOutputFile
                                  File.Copy(inputFile, outputFile, true)))
              else
                 printfn "skipping unchanged file %s" inputFile
                 let inputFile = Path.GetFullPath(inputFile)
                 match previous.TryFind inputFile with
                 | None -> ()
                 | Some res ->
                     yield (Some (inputFile, res), (fun _ -> ()))
          ]
  let rec processDirectory (htmlTemplate, texTemplate, pynbTemplate, fsxTemplate) indir outdir =
       [
        // Look for the presence of the _template.* files to activate the
        // generation of the content.
        let possibleNewHtmlTemplate = Path.Combine(indir, "_template.html")
        let htmlTemplate = if (try File.Exists(possibleNewHtmlTemplate) with _ -> false) then Some possibleNewHtmlTemplate else htmlTemplate
        let possibleNewPynbTemplate = Path.Combine(indir, "_template.ipynb")
        let pynbTemplate = if (try File.Exists(possibleNewPynbTemplate) with _ -> false) then Some possibleNewPynbTemplate else pynbTemplate
        let possibleNewFsxTemplate = Path.Combine(indir, "_template.fsx")
        let fsxTemplate = if (try File.Exists(possibleNewFsxTemplate) with _ -> false) then Some possibleNewFsxTemplate else fsxTemplate
        let possibleNewLatexTemplate = Path.Combine(indir, "_template.tex")
        let texTemplate = if (try File.Exists(possibleNewLatexTemplate) with _ -> false) then Some possibleNewLatexTemplate else texTemplate

        // Create output directory if it does not exist
        do
          if Directory.Exists(outdir) |> not then
              try Directory.CreateDirectory(outdir) |> ignore
              with _ -> failwithf "Cannot create directory '%s'" outdir

        let inputs = Directory.GetFiles(indir, "*") 
        let imageSaver = createImageSaver outdir

        for input in inputs do
            yield! processFile input OutputKind.Html htmlTemplate outdir imageSaver
            yield! processFile input OutputKind.Latex texTemplate outdir imageSaver
            yield! processFile input OutputKind.Pynb pynbTemplate outdir imageSaver
            yield! processFile input OutputKind.Fsx fsxTemplate outdir imageSaver

        for subdir in Directory.EnumerateDirectories(indir) do
            let name = Path.GetFileName(subdir)
            if name.StartsWith "." then
                printfn "skipping directory %s" subdir
            else
                yield! processDirectory (htmlTemplate, texTemplate, pynbTemplate, fsxTemplate) (Path.Combine(indir, name)) (Path.Combine(outdir, name))
       ]

  member _.Convert(input, htmlTemplate, extraInputs) =

    let inputDirectories = (input, ".") :: extraInputs
    [
      for (inputDirectory, outputPrefix) in inputDirectories do
        yield! processDirectory (htmlTemplate, None, None, None) inputDirectory outputPrefix
    ]

/// Processes and runs Suave server to host them on localhost
module Serve =

    let refreshEvent = new Event<_>()

    let socketHandler (webSocket : WebSocket) _ = socket {
      while true do
        do!
          refreshEvent.Publish
          |> Control.Async.AwaitEvent
          |> Suave.Sockets.SocketOp.ofAsync
        do! webSocket.send Text (ByteSegment (Encoding.UTF8.GetBytes "refreshed")) true }

    let startWebServer outputDirectory localPort =
        let defaultBinding = defaultConfig.bindings.[0]
        let withPort = { defaultBinding.socketBinding with port = uint16 localPort }
        let serverConfig =
            { defaultConfig with
                bindings = [ { defaultBinding with socketBinding = withPort } ]
                homeFolder = Some outputDirectory }
        let app =
            choose [
              path "/" >=> Redirection.redirect "/index.html"
              path "/websocket" >=> handShake socketHandler
              Writers.setHeader "Cache-Control" "no-cache, no-store, must-revalidate"
              >=> Writers.setHeader "Pragma" "no-cache"
              >=> Writers.setHeader "Expires" "0"
              >=> Files.browseHome ]
        startWebServerAsync serverConfig app |> snd |> Async.Start

type CoreBuildOptions(watch) =

    [<Option("input", Required=false, Default="docs", HelpText = "Input directory of documentation content.")>]
    member val input = "" with get, set

    [<Option("projects", Required=false, HelpText = "Project files to build API docs for outputs, defaults to all packable projects.")>]
    member val projects = Seq.empty<string> with get, set

    [<Option("output", Required = false, HelpText = "Output Directory (default 'output' for 'build' and 'tmp/watch' for 'watch'.")>]
    member val output = "" with get, set

    [<Option("noapidocs", Default= false, Required = false, HelpText = "Disable generation of API docs.")>]
    member val noapidocs = false with get, set

    [<Option("eval", Default=false, Required = false, HelpText = "Evaluate F# fragments in scripts.")>]
    member val eval = false with get, set

    [<Option("qualify", Default= false, Required = false, HelpText = "In API doc generation qualify the output by the collection name, e.g. 'reference/FSharp.Core/...' instead of 'reference/...' .")>]
    member val qualify = false with get, set

    [<Option("saveimages", Default= "none", Required = false, HelpText = "Save images referenced in docs (some|none|all). If 'some' then image links in formatted results are saved for latex and ipynb output docs.")>]
    member val saveImages = "none" with get, set

    [<Option("sourcefolder", Required = false, HelpText = "Source folder at time of component build (defaults to value of `<FsDocsSourceFolder>` from project file, else current directory)")>]
    member val sourceFolder = "" with get, set

    [<Option("sourcerepo", Required = false, HelpText = "Source repository for github links (defaults to value of `<FsDocsSourceRepository>` from project file, else `<RepositoryUrl>/tree/<RepositoryBranch>` for Git repositories)")>]
    member val sourceRepo = "" with get, set

    [<Option("nolinenumbers", Required = false, HelpText = "Don't add line numbers, default is to add line numbers.")>]
    member val nolinenumbers = false with get, set

    [<Option("nonpublic", Default=false, Required = false, HelpText = "The tool will also generate documentation for non-public members")>]
    member val nonpublic = false with get, set

    [<Option("mdcomments", Default=false, Required = false, HelpText = "Assume /// comments in F# code are markdown style (defaults to value of `<UsesMarkdownComments>` from project file)")>]
    member val mdcomments = false with get, set

    [<Option("parameters", Required = false, HelpText = "Additional substitution parameters for templates.")>]
    member val parameters = Seq.empty<string> with get, set

    [<Option("nodefaultcontent", Required = false, HelpText = "Do not copy default content styles, javascript or use default templates.")>]
    member val nodefaultcontent = false with get, set

    [<Option("fscoptions", Required=false, HelpText = "Extra flags for F# compiler analysis, e.g. dependency resolution.")>]
    member val fscoptions = Seq.empty<string> with get, set

    [<Option("clean", Required = false, Default=false, HelpText = "Clean the output directory.")>]
    member val clean = false with get, set

    member x.Execute() =
        let protect f = 
            try
                f()
                true
            with
                | :?AggregateException as ex ->
                    Log.errorf "received exception :\n %A" ex
                    printfn "Error : \n%O" ex
                    false
                | _ as ex ->
                    Log.errorf "received exception :\n %A" ex
                    printfn "Error : \n%O" ex
                    false


        /// The parameters as given by the user
        let userParameters =
            (evalPairwiseStringsNoOption x.parameters 
                |> List.map (fun (a,b) -> (ParamKey a, b)))

        // Adjust the user parameters for 'watch' mode root
        let root, userParameters =
            if watch then
                let r = sprintf "http://localhost:%d/" x.port_option
                if (dict userParameters).ContainsKey(ParamKey.root) then
                   printfn "ignoring user-specified root since in watch mode, root = %s" r
                let userParameters =
                    [ ParamKey.``root``,  r] @
                    (userParameters |> List.filter (fun (a, _) -> a <> ParamKey.``root``))
                r, userParameters
            else
                let r =
                    match (dict userParameters).TryGetValue(ParamKey.root) with
                    | true, v -> v
                    | _ -> "/"
                r, userParameters

        let userCollectionName = match (dict userParameters).TryGetValue(ParamKey.``fsdocs-collection-name``) with true, v -> Some v | _ -> None

        let (collectionName, crackedProjects, paths, docsParameters), _key =
          let projects = Seq.toList x.projects
          let cacheFile = ".fsdocs/cache"
          let getTime p = try File.GetLastWriteTimeUtc(p) with _ -> DateTime.Now
          let key1 =
             (root, x.parameters, projects,
              getTime (typeof<CoreBuildOptions>.Assembly.Location),
              (projects |> List.map getTime |> List.toArray))
          Utils.cacheBinary cacheFile
           (fun (_, key2) -> key1 = key2)
           (fun () ->
            if not x.noapidocs then
                Crack.crackProjects (root, userCollectionName, userParameters, projects), key1
            else
                ("", [], [], userParameters), key1)

        let apiDocInputs =
            [ for (dllFile, repoUrlOption, repoBranchOption, repoTypeOption, projectMarkdownComments, projectSourceFolder, projectSourceRepo, projectParameters) in crackedProjects -> 
                let sourceRepo =
                    match projectSourceRepo with
                    | Some s -> Some s
                    | None -> 
                    match evalString x.sourceRepo with
                    | Some v -> Some v
                    | None ->
                        //printfn "repoBranchOption = %A" repoBranchOption
                        match repoUrlOption, repoBranchOption, repoTypeOption with
                        | Some url, Some branch, Some "git" when not (String.IsNullOrWhiteSpace branch) ->
                            url + "/" + "tree/" +  branch |> Some
                        | Some url, _, Some "git" ->
                            url + "/" + "tree/" + "master" |> Some
                        | Some url, _, None -> Some url
                        | _ -> None

                let sourceFolder =
                    match projectSourceFolder with
                    | Some s -> s
                    | None -> 
                    match evalString x.sourceFolder with
                    | None -> Environment.CurrentDirectory
                    | Some v -> v

                printfn "sourceFolder = '%s'" sourceFolder
                printfn "sourceRepo = '%A'" sourceRepo
                { Path = dllFile;
                  XmlFile = None;
                  SourceRepo = sourceRepo;
                  SourceFolder = Some sourceFolder;
                  Parameters = Some projectParameters;
                  MarkdownComments = x.mdcomments || projectMarkdownComments;
                  PublicOnly = not x.nonpublic } ]

        let output =
           if x.output = "" then
              if watch then "tmp/watch" else "output"
           else x.output

        // This is in-package
        let dir = Path.GetDirectoryName(typeof<CoreBuildOptions>.Assembly.Location)
        let defaultTemplateAttempt1 = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "templates", "_template.html"))
        // This is in-repo only
        let defaultTemplateAttempt2 = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "docs", "_template.html"))
        let defaultTemplate =
           if x.nodefaultcontent then
              None
           else
              if (try File.Exists(defaultTemplateAttempt1) with _ -> false) then
                  Some defaultTemplateAttempt1
              elif (try File.Exists(defaultTemplateAttempt2) with _ -> false) then
                  Some defaultTemplateAttempt2
              else None

        let extraInputs =
           [ if not x.nodefaultcontent then
              // The "extras" content goes in "."
              let attempt1 = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "extras"))
              if (try Directory.Exists(attempt1) with _ -> false) then
                  printfn "using extra content from %s" attempt1
                  (attempt1, ".")
              else 
                  // This is for in-repo use only, assuming we are executing directly from
                  //   src\FSharp.Formatting.CommandTool\bin\Debug\netcoreapp3.1\fsdocs.exe 
                  //   src\FSharp.Formatting.CommandTool\bin\Release\netcoreapp3.1\fsdocs.exe 
                  let attempt2 = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "docs", "content"))
                  if (try Directory.Exists(attempt2) with _ -> false) then
                      printfn "using extra content from %s" attempt2
                      (attempt2, "content")
                  else
                      printfn "no extra content found at %s or %s" attempt1 attempt2
            ]

        // The incremental state (as well as the files written to disk)
        let mutable latestApiDocGlobalParameters = [ ]
        let mutable latestApiDocPhase2 = (fun _ -> ())
        let mutable latestApiDocSearchIndexEntries = [| |]
        let mutable latestDocContentPhase2 = (fun _ -> ())
        let mutable latestDocContentResults = Map.empty
        let mutable latestDocContentSearchIndexEntries = [| |]
        let mutable latestDocContentGlobalParameters = []

        // Actions to read out the incremental state
        let getLatestGlobalParameters() =
            latestApiDocGlobalParameters @
            latestDocContentGlobalParameters

        let regenerateSearchIndex() =
            let index = Array.append latestApiDocSearchIndexEntries  latestDocContentSearchIndexEntries
            let indxTxt = index |> Newtonsoft.Json.JsonConvert.SerializeObject
            File.WriteAllText(Path.Combine(output, "index.json"), indxTxt)

        // Incrementally convert content
        let runDocContentPhase1 () =
            protect (fun () ->
                //printfn "projectInfos = %A" projectInfos

                let saveImages = (match x.saveImages with "some" -> None | "none" -> Some false | "all" -> Some true | _ -> None)
                let models =
                    DocContent(output, latestDocContentResults,
                        Some (not x.nolinenumbers),
                        (if x.eval then Some ( FsiEvaluator() :> _) else None),
                        docsParameters,
                        saveImages).Convert(x.input, defaultTemplate, extraInputs)

                let extrasForSearchIndex =
                    [| for (thing, _action) in models do
                         match thing with
                         | Some (_inputFile, model) ->
                            match model.IndexText with
                            | Some text -> {title=model.Title; content = text; uri=model.Uri(root) }
                            | _ -> ()
                         | _ -> () |]

                let results =
                    Map.ofList [
                       for (thing, _action) in models do
                          match thing with
                          | Some res -> res
                          | None -> () ]

                let list_of_documents =
                    [ for (thing, _action) in models do
                         match thing with
                         | Some (_inputFile, model) -> li [] [ a [Href model.OutputPath] [encode model.Title ] ]
                         | _ -> ()
                    ]
                    |> List.map (fun html -> html.ToString()) |> String.concat "             \n"

                latestDocContentResults <- results
                latestDocContentSearchIndexEntries <- extrasForSearchIndex
                latestDocContentGlobalParameters <- [ ParamKey.``fsdocs-list-of-documents`` ,list_of_documents ]
                latestDocContentPhase2 <- (fun globals ->

                    for (_thing, action) in models do
                        action globals

                )
            )

        let runDocContentPhase2 () =
            protect (fun () ->
                let globals = getLatestGlobalParameters()
                latestDocContentPhase2 globals
            )

        // Incrementally generate API docs (actually regenerates everything)
        let runGeneratePhase1 () =
            protect (fun () ->
                if crackedProjects.Length > 0 then

                    let initialTemplate2 =
                        let t1 = Path.Combine(x.input, "reference", "_template.html")
                        let t2 = Path.Combine(x.input, "_template.html")
                        if File.Exists(t1) then
                            Some t1
                        elif File.Exists(t2) then
                            Some t2
                        else
                            match defaultTemplate with
                            | Some d ->
                                printfn "note, no template file '%s' or '%s', using default template %s" t1 t2 d
                                Some d
                            | None -> 
                                printfn "note, no template file '%s' or '%s', and no default template at '%s'" t1 t2 defaultTemplateAttempt1
                                None

                    if not x.noapidocs then

                        let globals, index, phase2 =
                          ApiDocs.GenerateHtmlPhased (
                            inputs = apiDocInputs,
                            output = output,
                            collectionName = collectionName,
                            parameters = docsParameters,
                            qualify = x.qualify,
                            ?template = initialTemplate2,
                            otherFlags = Seq.toList x.fscoptions,
                            root = root,
                            libDirs = paths
                            )

                        latestApiDocSearchIndexEntries <- index
                        latestApiDocGlobalParameters <- globals
                        latestApiDocPhase2 <- phase2 
            )

        let runGeneratePhase2 () =
            protect (fun () ->
                let globals = getLatestGlobalParameters()
                latestApiDocPhase2 globals 
                regenerateSearchIndex()
            )

        //-----------------------------------------
        // Clean

        let fullOut = Path.GetFullPath output
        let fullIn = Path.GetFullPath x.input

        if x.clean then
            let rec clean dir =
                for file in Directory.EnumerateFiles(dir) do
                    File.Delete file |> ignore
                for subdir in Directory.EnumerateDirectories dir do
                   if not (Path.GetFileName(subdir).StartsWith ".") then
                       clean subdir
            if output <> "/" && output <> "." && fullOut <> fullIn && not (String.IsNullOrEmpty output) then
                try clean fullOut
                with e -> printfn "warning: error during cleaning, continuing: %s" e.Message
            else
                printfn "warning: skipping cleaning due to strange output path: \"%s\"" output

        if watch then
            printfn "Building docs first time..." 

        //-----------------------------------------
        // Build

        let ok =
            let ok1 = runDocContentPhase1() 
            let ok2 = runGeneratePhase1() 
            let ok1 = ok1 && runDocContentPhase2() 
            let ok2 = ok2 && runGeneratePhase2()
            regenerateSearchIndex()
            ok1 && ok2

        //-----------------------------------------
        // Watch

        if watch then

            use docsWatcher = (if watch then new FileSystemWatcher(x.input) else null )
            use templateWatcher = (if watch then new FileSystemWatcher(x.input) else null )

            let projectOutputWatchers = (if watch then [ for input in apiDocInputs -> (new FileSystemWatcher(x.input), input.Path) ] else [] )
            use _holder = { new IDisposable with member __.Dispose() = for (p,_) in projectOutputWatchers do p.Dispose() }

            // Only one update at a time
            let monitor = obj()
            // One of each kind of request at a time
            let mutable docsQueued = true
            let mutable generateQueued = true

            let docsDependenciesChanged = Event<_>()
            docsDependenciesChanged.Publish.Add(fun () -> 
                    if not docsQueued then
                        docsQueued <- true
                        printfn "Detected change in '%s', scheduling rebuild of docs..."  x.input
                        Async.Start(async {
                          lock monitor (fun () ->
                            docsQueued <- false
                            if runDocContentPhase1() then
                              if runDocContentPhase2() then
                                 regenerateSearchIndex()
                            ) }) ) 

            let apiDocsDependenciesChanged = Event<_>()
            apiDocsDependenciesChanged.Publish.Add(fun () -> 
                    if not generateQueued then
                        generateQueued <- true
                        printfn "Detected change in built outputs, scheduling rebuild of API docs..."  
                        Async.Start(async {
                          lock monitor (fun () ->
                            generateQueued <- false
                            if runGeneratePhase1() then
                               if runGeneratePhase2() then
                                 regenerateSearchIndex()) }))


            // Listen to changes in any input under docs
            docsWatcher.IncludeSubdirectories <- true
            docsWatcher.NotifyFilter <- NotifyFilters.LastWrite
            docsWatcher.Changed.Add (fun _ ->docsDependenciesChanged.Trigger())

            // When _template.* change rebuild everything
            templateWatcher.IncludeSubdirectories <- true
            templateWatcher.Filter <- "_template.*"
            templateWatcher.NotifyFilter <- NotifyFilters.LastWrite
            templateWatcher.Changed.Add (fun _ ->
                docsDependenciesChanged.Trigger()
                apiDocsDependenciesChanged.Trigger())

            // Listen to changes in output DLLs
            for (projectOutputWatcher, projectOutput) in projectOutputWatchers do
               projectOutputWatcher.Filter <- Path.GetFileName(projectOutput)
               projectOutputWatcher.Path <- Path.GetDirectoryName(projectOutput)
               projectOutputWatcher.NotifyFilter <- NotifyFilters.LastWrite
               projectOutputWatcher.Changed.Add (fun _ -> apiDocsDependenciesChanged.Trigger())

            // Start raising events
            docsWatcher.EnableRaisingEvents <- true
            templateWatcher.EnableRaisingEvents <- true
            for (pathWatcher, _path) in projectOutputWatchers do
                pathWatcher.EnableRaisingEvents <- true

            generateQueued <- false
            docsQueued <- false
            if not x.noserver_option then
                printfn "starting server on http://localhost:%d for content in %s" x.port_option fullOut
                Serve.startWebServer fullOut x.port_option
            if not x.nolaunch_option then
                let url = sprintf "http://localhost:%d/%s" x.port_option x.open_option
                printfn "launching browser window to open %s" url
                let OpenBrowser(url: string) =
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) then
                        Process.Start(new ProcessStartInfo(url, UseShellExecute = true)) |> ignore
                    elif (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) then
                        Process.Start("xdg-open", url)  |> ignore
                    elif (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) then
                        Process.Start("open", url) |> ignore
            
                OpenBrowser (url)
            waitForKey watch

        if ok then 0 else -1 

    abstract noserver_option : bool
    default x.noserver_option = false

    abstract nolaunch_option : bool
    default x.nolaunch_option = false

    abstract open_option : string
    default x.open_option = ""

    abstract port_option : int
    default x.port_option = 0

[<Verb("build", HelpText = "build the documentation for a solution based on content and defaults")>]
type BuildCommand() =
    inherit CoreBuildOptions(false)

[<Verb("watch", HelpText = "build the documentation for a solution based on content and defaults, watch it and serve it")>]
type WatchCommand() =
    inherit CoreBuildOptions(true)

    override x.noserver_option = x.noserver
    [<Option("noserver", Required = false, Default=false, HelpText = "Do not serve content when watching.")>]
    member val noserver = false with get, set

    override x.nolaunch_option = x.nolaunch
    [<Option("nolaunch", Required = false, Default=false, HelpText = "Do not launch a browser window.")>]
    member val nolaunch = false with get, set

    override x.open_option = x.openv
    [<Option("open", Required = false, Default="", HelpText = "URL extension to launch http://localhost:<port>/%s.")>]
    member val openv = "" with get, set

    override x.port_option = x.port
    [<Option("port", Required = false, Default=8901, HelpText = "Port to serve content for http://localhost serving.")>]
    member val port = 8901 with get, set



