﻿namespace FSharp.CompilerBinding
open System
open System.IO
open System.Diagnostics
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.SourceCodeServices
open ExtCore
open ExtCore.Control
open ExtCore.Control.Collections

module Symbols =
  /// We always know the text of the identifier that resolved to symbol.
  /// Trim the range of the referring text to only include this identifier.
  /// This means references like A.B.C are trimmed to "C".  This allows renaming to just rename "C". 
  let trimSymbolRegion(symbolUse:FSharpSymbolUse) (lastIdentAtLoc:string) =
    let m = symbolUse.RangeAlternate 
    let ((beginLine, beginCol), (endLine, endCol)) = ((m.StartLine, m.StartColumn), (m.EndLine, m.EndColumn))
             
    let (beginLine, beginCol) =
        if endCol >=lastIdentAtLoc.Length && (beginLine <> endLine || (endCol-beginCol) >= lastIdentAtLoc.Length) then 
          (endLine,endCol-lastIdentAtLoc.Length)
        else
          (beginLine, beginCol)
    Range.mkPos beginLine beginCol, Range.mkPos endLine endCol
    //(beginLine, beginCol), (endLine, endCol)

/// Contains settings of the F# language service
module ServiceSettings =
  let internal getEnvInteger e dflt = match System.Environment.GetEnvironmentVariable(e) with null -> dflt | t -> try int t with _ -> dflt
  /// When making blocking calls from the GUI, we specify this value as the timeout, so that the GUI is not blocked forever
  let blockingTimeout = getEnvInteger "FSharpBinding_BlockingTimeout" 1000
  let maximumTimeout = getEnvInteger "FSharpBinding_MaxTimeout" 10000

// --------------------------------------------------------------------------------------
/// Wraps the result of type-checking and provides methods for implementing
/// various IntelliSense functions (such as completion & tool tips). Provides default
/// empty/negative results if information is missing.
type ParseAndCheckResults private (infoOpt: (FSharpCheckFileResults * FSharpParseFileResults) option) =
  let token = Parser.tagOfToken(Parser.token.IDENT("")) 

  new (checkResults, parseResults) = ParseAndCheckResults(Some (checkResults, parseResults))

  static member Empty = ParseAndCheckResults(None)

  /// Get declarations at the current location in the specified document and the long ident residue
  /// e.g. The incomplete ident One.Two.Th will return Th
  member x.GetDeclarations(line, col, lineStr) = 
    match infoOpt with 
    | None -> None
    | Some (checkResults, parseResults) -> 
      let longName,residue = Parsing.findLongIdentsAndResidue(col, lineStr)
      Debug.WriteLine (sprintf "GetDeclarations: '%A', '%s'" longName residue)
      // Get items & generate output
      try
        let results =
          Async.RunSynchronously (checkResults.GetDeclarationListInfo(Some parseResults, line, col, lineStr, longName, residue, fun (_,_) -> false), timeout = ServiceSettings.blockingTimeout )
        Some (results, residue)
      with :? TimeoutException -> None

    /// Get the symbols for declarations at the current location in the specified document and the long ident residue
    /// e.g. The incomplete ident One.Two.Th will return Th
    member x.GetDeclarationSymbols(line, col, lineStr) = 
      match infoOpt with 
      | None -> None
      | Some (checkResults, parseResults) -> 
          let longName,residue = Parsing.findLongIdentsAndResidue(col, lineStr)
          Debug.WriteLine (sprintf "GetDeclarationSymbols: '%A', '%s'" longName residue)
          // Get items & generate output
          try
           let results = 
               Async.RunSynchronously (checkResults.GetDeclarationListSymbols(Some parseResults, line, col, lineStr, longName, residue, fun (_,_) -> false),
                                       timeout = ServiceSettings.blockingTimeout )
           Some (results, residue)
          with :? TimeoutException -> None

    /// Get the tool-tip to be displayed at the specified offset (relatively
    /// from the beginning of the current document)
    member x.GetToolTip(line, col, lineStr) =
      async {
        match infoOpt with 
        | None -> return None
        | Some (checkResults, _parseResults) -> 
        match Parsing.findLongIdents(col, lineStr) with 
        | None -> return None
        | Some(col,identIsland) ->
          let! res = checkResults.GetToolTipTextAlternate(line, col, lineStr, identIsland, token)
          let! sym = checkResults.GetSymbolUseAtLocation(line, col, lineStr, identIsland)
          Debug.WriteLine("Result: Got something, returning")
          return sym |> Option.bind (fun sym -> let start, finish = Symbols.trimSymbolRegion sym (Seq.last identIsland)
                                                Some (res, (start.Column, finish.Column))) }
      
    member x.GetDeclarationLocation(line, col, lineStr) =
      async {
        match infoOpt with 
        | None -> return FSharpFindDeclResult.DeclNotFound FSharpFindDeclFailureReason.Unknown
        | Some (checkResults, _parseResults) -> 
        match Parsing.findLongIdents(col, lineStr) with 
        | None -> return FSharpFindDeclResult.DeclNotFound FSharpFindDeclFailureReason.Unknown
        | Some(col,identIsland) -> return! checkResults.GetDeclarationLocationAlternate(line, col, lineStr, identIsland, false) }
      
    member x.GetMethods(line, col, lineStr) =
      async { 
        match infoOpt with 
        | None -> return None
        | Some (checkResults, _parseResults) -> 
        match Parsing.findLongIdentsAtGetMethodsTrigger(col, lineStr) with 
        | None -> return None
        | Some(col,identIsland) ->
            let! res = checkResults.GetMethodsAlternate(line, col, lineStr, Some identIsland)
            Debug.WriteLine("Result: Got something, returning")
            return Some (res.MethodName, res.Methods) }

    member x.GetSymbolAtLocation(line, col, lineStr) =
      async {
        match infoOpt with 
        | None -> return None
        | Some (checkResults, _parseResults) -> 
        match Parsing.findLongIdents(col, lineStr) with 
        | None -> return None
        | Some(colu, identIsland) ->
            return! checkResults.GetSymbolUseAtLocation(line, colu, lineStr, identIsland) }

    member x.GetMethodsAsSymbols(line, col, lineStr) =
      async {
        match infoOpt with 
        | None -> return None
        | Some (checkResults, _parseResults) -> 
        match Parsing.findLongIdentsAtGetMethodsTrigger(col, lineStr) with 
        | None -> return None
        | Some(colu, identIsland) ->
            return! checkResults.GetMethodsAsSymbols(line, colu, lineStr, identIsland) }

    member x.GetUsesOfSymbolInFile(symbol) =
      async {
        match infoOpt with 
        | None -> return [| |]
        | Some (checkResults, _parseResults) -> return! checkResults.GetUsesOfSymbolInFile(symbol) }

    member x.GetAllUsesOfAllSymbolsInFile() =
      async {
        match infoOpt with
        | None -> return None
        | Some (checkResults, _parseResults) ->
            let! allSymbols = checkResults.GetAllUsesOfAllSymbolsInFile()
            return Some allSymbols }

    member x.PartialAssemblySignature =
      async {
        match infoOpt with
        | None -> return None
        | Some (checkResults, _parseResults) ->
          return Some checkResults.PartialAssemblySignature }

    member x.GetErrors() =
      match infoOpt with 
      | None -> None
      | Some (checkResults, _parseResults) -> Some checkResults.Errors

    member x.GetNavigationItems() =
      match infoOpt with 
      | None -> [| |]
      | Some (_checkResults, parseResults) -> 
         // GetNavigationItems is not 100% solid and throws occasional exceptions
          try parseResults.GetNavigationItems().Declarations
          with _ -> 
            Debug.Assert(false, "couldn't update navigation items, ignoring")  
            [| |]

    member x.ParseTree = 
      match infoOpt with
      | Some (_checkResults,parseResults) -> parseResults.ParseTree
      | None -> None

    member x.CheckResults = 
      match infoOpt with
      | Some (checkResults,_parseResults) -> checkResults |> Some
      | None -> None

    member x.GetExtraColorizations() =
      match infoOpt with
      | Some(checkResults,_parseResults) -> checkResults.GetExtraColorizationsAlternate() |> Some
      | None -> None

[<RequireQualifiedAccess>]
type AllowStaleResults = 
  // Allow checker results where the source doesn't even match
  | MatchingFileName
  // Allow checker results where the source matches but where the background builder may not have caught up yet after some other change
  | MatchingSource
  // Don't allow stale results
  | No

//type Debug = System.Console

/// Provides functionality for working with the F# interactive checker running in background
type LanguageService(dirtyNotify) =

  /// Load times used to reset type checking properly on script/project load/unload. It just has to be unique for each project load/reload.
  /// Not yet sure if this works for scripts.
  let fakeDateTimeRepresentingTimeLoaded proj = DateTime(abs (int64 (match proj with null -> 0 | _ -> proj.GetHashCode())) % 103231L)

  // Create an instance of interactive checker. The callback is called by the F# compiler service
  // when its view of the prior-typechecking-state of the start of a file has changed, for example
  // when the background typechecker has "caught up" after some other file has been changed, 
  // and its time to re-typecheck the current file.
  let checker = 
    let checker = FSharpChecker.Create()
    checker.BeforeBackgroundFileCheck.Add dirtyNotify
    checker

  /// When creating new script file on Mac, the filename we get sometimes 
  /// has a name //foo.fsx, and as a result 'Path.GetFullPath' throws in the F#
  /// language service - this fixes the issue by inventing nicer file name.
  let fixFileName path = 
    if (try Path.GetFullPath(path) |> ignore; true
        with _ -> false) then path
    else 
      let dir = 
        if Environment.OSVersion.Platform = PlatformID.Unix ||  
           Environment.OSVersion.Platform = PlatformID.MacOSX then
          Environment.GetEnvironmentVariable("HOME") 
        else
          Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%")
      Path.Combine(dir, Path.GetFileName(path))

  let projectInfoCache =
    //cache 50 project infos, then start evicting the least recently used entries
    ref (ExtCore.Caching.LruCache.create 50u)
   
  // We use a mailbox processor to wrap requests to F.C.S. here so 
  //   (a) we can get work off the GUI thread and 
  //   (b) timeout on synchronous requests. 
  // 
  // There is already a background compilation queue+thread in F.C.S. abd many of the F.C.S. operations
  // are already asynchronous. However using direct calls to the F.C.S. API isn't quite sufficient because
  // we can't timeout, and not all F.C.S. operations are asynchronous (e.g. parsing). If F.C.S. is extended
  // so all operations are asynchronous then I believe we won't need a wrapper agent at all.
  //
  // Every request to this agent is 'PostAndReply' or 'PostAndAsyncReply'.  This means the requests are
  // a lot like a function call, except 
  //   (a) they may be asynchronous (reply is interleaved on the UI thread)
  //   (b) they may be on on a timeout (to prevent blocking the UI thread)
  //   (c) only one request is active at a time, the rest are in the queue

  let mbox = MailboxProcessor.Start(fun mbox ->
    async { 
      while true do
        try
           let! (fileName, source, options, reply: AsyncReplyChannel<_> ) = mbox.Receive()
           let fileName = fixFileName(fileName)
           let! parseResults, checkAnswer = checker.ParseAndCheckFileInProject(fileName, 0, source,options, IsResultObsolete(fun () -> false), null )

           // Construct new typed parse result if the task succeeded
           let results =
             match checkAnswer with
             | FSharpCheckFileAnswer.Succeeded(checkResults) ->
                 ParseAndCheckResults(checkResults, parseResults)
             | _ -> 
                 ParseAndCheckResults.Empty
                 
           reply.Reply results
         with exn -> Debug.WriteLine("LanguageService agent: Exception: {0}", exn.ToString()) } )

  static member IsAScript fileName =
    let ext = Path.GetExtension fileName
    [".fsx";".fsscript";".sketchfs"] |> List.exists ((=) ext)

  member x.RemoveFromProjectInfoCache(projFilename:string, ?properties) =
    let properties = defaultArg properties ["Configuration", "Debug"]
    let key = (projFilename, properties)
    Debug.WriteLine("LanguageService: Removing {0} from projectInfoCache", projFilename)
    match (!projectInfoCache).TryExtract(key) with
    | Some _extractee, cache -> projectInfoCache := cache
    | None, _unchangedCache -> ()

  member x.ClearProjectInfoCache() =
    Debug.WriteLine("LanguageService: Clearing ProjectInfoCache")
    projectInfoCache := ExtCore.Caching.LruCache.create 50u

  /// Constructs options for the interactive checker for the given file in the project under the given configuration.
  member x.GetCheckerOptions(fileName, projFilename, source) =
    Debug.WriteLine("LanguageService: GetCheckerOptions")
    let opts =
      if LanguageService.IsAScript fileName then
        // We are in a stand-alone file or we are in a project, but currently editing a script file
        x.GetScriptCheckerOptions(fileName, projFilename, source)
          
      // We are in a project - construct options using current properties
      else
        x.GetProjectCheckerOptions(projFilename)
    opts
   
  /// Constructs options for the interactive checker for the given script file in the project under the given configuration. 
  member x.GetScriptCheckerOptions(fileName, projFilename, source) =
    let opts = 
      // We are in a stand-alone file or we are in a project, but currently editing a script file
      try 
        let fileName = fixFileName(fileName)
        Debug.WriteLine ("LanguageService: GetScriptCheckerOptions: Creating for stand-alone file or script: {0}", fileName)
        let opts =
          Async.RunSynchronously (checker.GetProjectOptionsFromScript(fileName, source, fakeDateTimeRepresentingTimeLoaded projFilename),
                                  timeout = ServiceSettings.maximumTimeout)

      // The InteractiveChecker resolution sometimes doesn't include FSharp.Core and other essential assemblies, so we need to include them by hand
        if opts.OtherOptions |> Seq.exists (fun s -> s.Contains("FSharp.Core.dll")) then opts
        else 
          // Add assemblies that may be missing in the standard assembly resolution
          Debug.WriteLine("LanguageService: GetScriptCheckerOptions: Adding missing core assemblies.")
          let dirs = FSharpEnvironment.getDefaultDirectories (None, FSharpTargetFramework.NET_4_5 )
          {opts with OtherOptions = [| yield! opts.OtherOptions
                                       match FSharpEnvironment.resolveAssembly dirs "FSharp.Core" with
                                       | Some fn -> yield String.Format ("-r:{0}", fn)
                                       | None -> Debug.WriteLine("LanguageService: Resolution: FSharp.Core assembly resolution failed!")
                                       match FSharpEnvironment.resolveAssembly dirs "FSharp.Compiler.Interactive.Settings" with
                                       | Some fn -> yield String.Format ("-r:{0}", fn)
                                       | None -> Debug.WriteLine("LanguageService: Resolution: FSharp.Compiler.Interactive.Settings assembly resolution failed!") |]}
      with e -> failwithf "Exception when getting check options for '%s'\n.Details: %A" fileName e

    // Print contents of check option for debugging purposes
    // Debug.WriteLine(sprintf "GetScriptCheckerOptions: ProjectFileName: %s, ProjectFileNames: %A, ProjectOptions: %A, IsIncompleteTypeCheckEnvironment: %A, UseScriptResolutionRules: %A" 
    //                      opts.ProjectFileName opts.ProjectFileNames opts.ProjectOptions opts.IsIncompleteTypeCheckEnvironment opts.UseScriptResolutionRules)
    opts
   
  /// Constructs options for the interactive checker for a project under the given configuration. 
  member x.GetProjectCheckerOptions(projFilename, ?properties) =
    let properties = defaultArg properties ["Configuration", "Debug"]
    let key = (projFilename, properties)
    lock projectInfoCache (fun () ->
      match (!projectInfoCache).TryFind (key) with
      | Some entry, cache ->
          Debug.WriteLine ("LanguageService: GetProjectCheckerOptions: Getting ProjectOptions from cache for {0}", Path.GetFileName(projFilename))
          projectInfoCache := cache
          entry
      | None, cache ->
          Debug.WriteLine ("LanguageService: GetProjectCheckerOptions: Generating ProjectOptions for {0}", Path.GetFileName(projFilename))
          let opts = checker.GetProjectOptionsFromProjectFile(projFilename, properties)
          projectInfoCache := cache.Add (key, opts)
          opts)

    // Print contents of check option for debugging purposes
    // Debug.WriteLine(sprintf "GetProjectCheckerOptions: ProjectFileName: %s, ProjectFileNames: %A, ProjectOptions: %A, IsIncompleteTypeCheckEnvironment: %A, UseScriptResolutionRules: %A" 
    //                      opts.ProjectFileName opts.ProjectFileNames opts.ProjectOptions opts.IsIncompleteTypeCheckEnvironment opts.UseScriptResolutionRules)


  member x.StartBackgroundCompileOfProject (projectFilename) =
    let opts = x.GetProjectCheckerOptions(projectFilename)
    checker.StartBackgroundCompile(opts)

  /// Parses and checks the given file in the given project under the given configuration. Asynchronously
  /// returns the results of checking the file.
  member x.ParseAndCheckFileInProject(projectFilename, fileName:string, src) =
    async {
      let opts = x.GetCheckerOptions(fileName, projectFilename, src)
      Debug.WriteLine("LanguageService: ParseAndCheckFileInProject: Trigger parse (fileName={0})", fileName)
      let! results = mbox.PostAndAsyncReply(fun r -> fileName, src, opts, r)
      return results }

  member x.ParseFileInProject(projectFilename, fileName:string, src) = 
    let opts = x.GetCheckerOptions(fileName, projectFilename, src)
    Debug.WriteLine("LanguageService: ParseFileInProject: Get untyped parse result (fileName={0})", fileName)
    checker.ParseFileInProject(fixFileName fileName, src, opts)

  member internal x.TryGetStaleTypedParseResult(fileName:string, options, src, stale)  = 
    // Try to get recent results from the F# service
    let res = 
      match stale with 
      | AllowStaleResults.MatchingFileName -> checker.TryGetRecentTypeCheckResultsForFile(fixFileName fileName, options) 
      | AllowStaleResults.MatchingSource -> checker.TryGetRecentTypeCheckResultsForFile(fixFileName fileName, options, source=src) 
      | AllowStaleResults.No -> None
    match res with 
    | Some (untyped,typed,_) when typed.HasFullTypeCheckInfo  -> Some (ParseAndCheckResults(typed, untyped))
    | _ -> None

  member x.GetTypedParseResultWithTimeout(projectFilename, fileName:string, src, stale, ?timeout) = 
    async {
      let fileName = if Path.GetExtension fileName = ".sketchfs" then Path.ChangeExtension (fileName, ".fsx") else fileName
      let opts = x.GetCheckerOptions(fileName, projectFilename, src)
      Debug.WriteLine("LanguageService: GetTypedParseResultWithTimeout, fileName={0}", fileName)
      // Try to get recent results from the F# service
      match x.TryGetStaleTypedParseResult(fileName, opts, src, stale) with
      | Some _ as results ->
          Debug.WriteLine("LanguageService: GetTypedParseResultWithTimeout: using stale results")
          return results
      | None -> 
          Debug.WriteLine("LanguageService: GetTypedParseResultWithTimeout: No stale results - trying typecheck with timeout")
          // If we didn't get a recent set of type checking results, we put in a request and wait for at most 'timeout' for a response
          match timeout with
          | Some timeout -> return mbox.TryPostAndReply((fun reply -> (fileName, src, opts, reply)), timeout = timeout)
          | None -> return mbox.TryPostAndReply((fun reply -> (fileName, src, opts, reply))) }

  /// Returns a TypeParsedResults if available, otherwise None
  member x.GetTypedParseResultIfAvailable(projectFilename, fileName:string, src, stale) = 
    let opts = x.GetCheckerOptions(fileName, projectFilename, src)
    Debug.WriteLine("LanguageService: GetTypedParseResultIfAvailable: fileName={0}", fileName)
    match x.TryGetStaleTypedParseResult(fileName, opts, src, stale)  with
    | Some results -> results
    | None -> ParseAndCheckResults.Empty


  /// Get all the uses of a symbol in the given file (using 'source' as the source for the file)
  member x.GetUsesOfSymbolAtLocationInFile(projectFilename, fileName, source, line:int, col, lineStr) =
    asyncMaybe {
      Debug.WriteLine("LanguageService: GetUsesOfSymbolAtLocationInFile: fileName={0}, line = {1}, col = {2}", fileName, line, col)
      let! colu, identIsland = Parsing.findLongIdents(col, lineStr) |> async.Return
      let! results = x.GetTypedParseResultWithTimeout(projectFilename, fileName, source, stale= AllowStaleResults.MatchingSource)
      let! symbolUse = results.GetSymbolAtLocation(line, colu, lineStr)
      let lastIdent = Seq.last identIsland
      let! refs = results.GetUsesOfSymbolInFile(symbolUse.Symbol) |> Async.map Some
      return (lastIdent, refs) }

  /// Get all the uses of the specified symbol in the current project and optionally all dependent projects
  member x.GetUsesOfSymbolInProject(projectFilename, file, source, symbol:FSharpSymbol, ?dependentProjects) =
    async { 
      Debug.WriteLine("LanguageService: GetUsesOfSymbolInProject: project={0}, currentFile = {1}, symbol = {2}", projectFilename, file, symbol.DisplayName )
      let sourceProjectOptions = x.GetCheckerOptions(file, projectFilename, source)
      let dependentProjects = defaultArg dependentProjects []
      
      let! allSymbolUses =
        sourceProjectOptions
        |> List.cons (dependentProjects |> List.map x.GetProjectCheckerOptions)
        |> Async.List.map checker.ParseAndCheckProject
        |> Async.RunSynchronously
        |> List.map (fun checkedProj -> checkedProj.GetUsesOfSymbol(symbol))
        |> Async.Parallel
        |> Async.map Array.concat
    
     return allSymbolUses }

  /// This function is called when the project is know to have changed for reasons not encoded in the ProjectOptions
  /// e.g. dependent references have changed
  member x.InvalidateConfiguration(options) =
    Debug.WriteLine("LanguageService: Invalidating configuration for: {0}", Path.GetFileName(options.ProjectFileName))
    checker.InvalidateConfiguration(options)

  //flush all caches and garbage collect
  member x.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients() =
    Debug.WriteLine("LanguageService: Clearing root caches and finalizing transients")
    checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()
