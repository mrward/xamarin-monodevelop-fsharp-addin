﻿namespace MonoDevelop.FSharp
open System
open System.IO
open System.Collections.Generic
open MonoDevelop
open MonoDevelop.Core
open MonoDevelop.Components.Commands
open MonoDevelop.Ide
open MonoDevelop.Ide.Editor
open MonoDevelop.Projects
open MonoDevelop.Refactoring
open FSharp.CompilerBinding
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.SourceCodeServices
open MonoDevelop.Ide.FindInFiles

module Refactoring =

    type SymbolDeclarationLocation =
    | CurrentFile
    | Projects of Project seq * isSymbolLocal:bool
    | External of string
    | Unknown      

    let langServ = MDLanguageService.Instance

    let private performChanges (symbol:FSharpSymbolUse) (locations:array<string * Microsoft.CodeAnalysis.Text.TextSpan> ) =
        Func<_,_>(fun (renameProperties:Rename.RenameRefactoring.RenameProperties) -> 
        let results =
            use monitor = new ProgressMonitoring.MessageDialogProgressMonitor (true, false, false, true)
            [|
                if renameProperties.RenameFile && symbol.IsFromType then
                    yield!
                        // TODO check .fsi file in renames?
                        Symbols.getLocationFromSymbolUse symbol
                        |> List.map (fun part -> RenameFileChange (part.FileName, renameProperties.NewName) :> Change)
                
                yield!
                    locations
                    |> Array.map (fun (name, location) ->
                        TextReplaceChange (FileName = name,
                                           Offset = location.Start,
                                           RemovedChars = location.Length,
                                           InsertedText = renameProperties.NewName,
                                           Description = String.Format ("Replace '{0}' with '{1}'", symbol.Symbol.DisplayName, renameProperties.NewName))
                        :> Change) |]
  
        results :> IList<Change> )

    let getDocumentationId (symbol:FSharpSymbol) =
        match symbol with
        | :? FSharpEntity as ent ->
            ent.XmlDocSig
        | :? FSharpMemberOrFunctionOrValue as meth ->
            meth.XmlDocSig
        | _ -> ""

    let getSymbolDeclarationLocation (symbol: FSharpSymbol) (currentFile: FilePath) (solution: Solution) =
        let isPrivateToFile = 
            match symbol with
            | :? FSharpMemberOrFunctionOrValue as m -> not m.IsModuleValueOrMember
            | :? FSharpEntity as m -> m.Accessibility.IsPrivate
            | :? FSharpGenericParameter -> true
            | :? FSharpUnionCase as m -> m.Accessibility.IsPrivate
            | :? FSharpField as m -> m.Accessibility.IsPrivate
            | _ -> false

        if isPrivateToFile then 
            SymbolDeclarationLocation.CurrentFile 
        else
            let isSymbolLocalForProject = TypedAstUtils.isSymbolLocalForProject symbol 
            match Option.orElse symbol.ImplementationLocation symbol.DeclarationLocation with
            | Some loc ->
                let filePath = Path.GetFullPathSafe loc.FileName
                if filePath = currentFile.ToString () then //Or if script?
                    SymbolDeclarationLocation.CurrentFile
                //elif currentProject.IsForStandaloneScript then
                    // The standalone script might include other files via '#load'
                    // These files appear in project options and the standalone file 
                    // should be treated as an individual project
                //    Some (SymbolDeclarationLocation.Projects ([currentProject], isSymbolLocalForProject))
                else
                    let allProjects = solution.GetAllProjects ()
                    match allProjects
                          |> Seq.filter (fun p -> p.Files |> Seq.exists (fun f -> f.FilePath.ToString () = filePath)) with
                    | projects when projects |> Seq.isEmpty ->
                        External (getDocumentationId symbol)
                    | projects -> SymbolDeclarationLocation.Projects (projects, isSymbolLocalForProject)
            | None -> SymbolDeclarationLocation.Unknown

    let canRename (symbolUse:FSharpSymbolUse) fileName project =
        match getSymbolDeclarationLocation symbolUse.Symbol fileName project with
        | SymbolDeclarationLocation.External _ -> false
        | SymbolDeclarationLocation.Unknown -> false
        | _ -> true

    let canJump (symbolUse:FSharpSymbolUse) currentFile solution =
        //Reference:
        //For Roslyn the following symbol types *cant* be jumped to: 
        //Alias, ArrayType, Assembly, DynamicType, ErrorType, NetModule, NameSpace, PointerType, PreProcessor
        match symbolUse.Symbol with
        | :? FSharpMemberOrFunctionOrValue
        | :? FSharpUnionCase
        | :? FSharpEntity
        | :? FSharpField
        | :? FSharpGenericParameter
        | :? FSharpActivePatternCase
        | :? FSharpParameter
        | :? FSharpStaticParameter ->
            match getSymbolDeclarationLocation symbolUse.Symbol currentFile solution with
            | SymbolDeclarationLocation.External _ -> true
            | SymbolDeclarationLocation.Unknown -> false
            | _ -> true
        | _ -> false
        
    let canGotoBase (symbolUse:FSharpSymbolUse) =
        match symbolUse.Symbol with
            | :? FSharpMemberOrFunctionOrValue as mfv when mfv.IsDispatchSlot ->
                match mfv.EnclosingEntity.BaseType with
                | Some bt -> bt.HasTypeDefinition
                | _ -> false
            | :? FSharpEntity as ent ->
                match ent.BaseType with
                | Some bt -> bt.HasTypeDefinition
                | _ -> false
            | _ -> false
         
    type BaseSymbol =
        | Member of FSharpMemberOrFunctionOrValue
        | Type of FSharpEntity
        
    let getBaseSymbol (symbolUse:FSharpSymbolUse) =
        match symbolUse.Symbol with
        | :? FSharpMemberOrFunctionOrValue as mfv when mfv.IsDispatchSlot ->
            match mfv.EnclosingEntity.BaseType with
            | Some bt when bt.HasTypeDefinition ->
                let baseDefs = bt.TypeDefinition.MembersFunctionsAndValues
                
                //TODO check for more than one match?
                let matches = baseDefs |> Seq.filter (fun btd -> btd.DisplayName = mfv.DisplayName) |> Seq.toList
                assert (matches.Length <= 1)
                
                //assume just the first for now
                match baseDefs |> Seq.tryFind (fun btd -> btd.DisplayName = mfv.DisplayName) with
                | Some bm -> Some (Member(bm))
                | _ -> None
            | _ -> None
        | :? FSharpEntity as ent ->
            match ent.BaseType with
            | Some bt when bt.HasTypeDefinition -> Some (Type(bt.TypeDefinition))
            | _ -> None
        | _ -> None
    
    let getSymbolAndLineInfoAtCaret (ast: ParseAndCheckResults) (editor:TextEditor) =
        let lineInfo = editor.GetLineInfoByCaretOffset ()
        let symbol = ast.GetSymbolAtLocation lineInfo |> Async.RunSynchronously
        lineInfo, symbol

    let rename (editor:TextEditor, ctx:DocumentContext, lastIdent, symbol:FSharpSymbolUse) =         
        let symbols = 
            let activeDocFileName = editor.FileName.ToString ()
            Async.RunSynchronously
                (langServ.GetUsesOfSymbolInProject (ctx.Project.FileName.ToString(), activeDocFileName, editor.Text, symbol.Symbol),
                 ServiceSettings.maximumTimeout)

        let locations =
            symbols |> Array.map (Symbols.getTextSpanTrimmed lastIdent)

        let fileLocations = 
            locations
            |> Array.map fst
            |> Array.toSet
                  
        if fileLocations.Count = 1 then
            let links = ResizeArray<TextLink> ()
            let link = TextLink ("name")

            for (_file, loc) in locations do
                let segment = Text.TextSegment (loc.Start, loc.Length)
                if (segment.Offset <= editor.CaretOffset && editor.CaretOffset <= segment.EndOffset) then
                    link.Links.Insert (0, segment)
                else
                    link.AddLink (segment)

            links.Add (link)
            editor.StartTextLinkMode (TextLinkModeOptions (links))
        else
            MessageService.ShowCustomDialog (new Rename.RenameItemDialog("Rename Item", symbol.Symbol.DisplayName, performChanges symbol locations))
            |> ignore

    let getJumpTypePartSearchResult (editor:TextEditor, ctx:DocumentContext, symbolUse:FSharpSymbolUse, location: Range.range) =
        
            let provider = FindInFiles.FileProvider (location.FileName)
            let doc = TextEditorFactory.CreateNewDocument ()
            //TODO: This is unfinished...
            //(doc :> ITextDocument).Text <- provider.ReadString ()
            //let fileName, start, finish = Symbols.getTrimmedRangesForDeclarations lastIdent symbolUse
                        
            FindInFiles.SearchResult (provider, 0, 0)
        

    let jumpTo (editor:TextEditor, ctx:DocumentContext, symbolUse, location:Range.range) =
            match getSymbolDeclarationLocation symbolUse editor.FileName ctx.Project.ParentSolution with
            | SymbolDeclarationLocation.CurrentFile ->
                IdeApp.Workbench.OpenDocument (Gui.FileOpenInformation (FilePath(location.FileName), ctx.Project, Line = location.StartLine, Column = location.StartColumn))
                |> ignore
                
            | SymbolDeclarationLocation.Projects (_projects, _isSymbolLocal) ->
                IdeApp.Workbench.OpenDocument (Gui.FileOpenInformation (FilePath(location.FileName), ctx.Project, Line = location.StartLine, Column = location.StartColumn))
                |> ignore

            | SymbolDeclarationLocation.External docId ->
                match symbolUse.Assembly.FileName with
                | Some filename ->
                    IdeApp.ProjectOperations.JumpToMetadata(filename, docId)
                | None -> 
                    ()
            | _ -> ()    

                
    let jumpToDeclaration (editor:TextEditor, ctx:DocumentContext, symbolUse:FSharpSymbolUse) =
            match Symbols.getLocationFromSymbolUse symbolUse with
            | [] -> ()
            | [loc] -> jumpTo (editor, ctx, symbolUse.Symbol, loc)
            | locations ->
                    use monitor = IdeApp.Workbench.ProgressMonitors.GetSearchProgressMonitor (true, true)
                    for part in locations do
                        monitor.ReportResult (getJumpTypePartSearchResult (editor, ctx, symbolUse, part))
                        |> ignore

    let getProjectOutputFilename config (p:Project) =
        p.GetOutputFileName(config).ToString() |> Path.GetFileNameWithoutExtension

    let findReferences (editor:TextEditor, ctx:DocumentContext, symbolUse:FSharpSymbolUse, lastIdent) =
        let monitor = IdeApp.Workbench.ProgressMonitors.GetSearchProgressMonitor (true, true)
        let findAsync = async { 
            let dependentProjects =
                try
                    let allProjects = ctx.Project.ParentSolution.GetAllProjects()
                    let config = IdeApp.Workspace.ActiveConfiguration
                    let currentContextProjectFilename = getProjectOutputFilename config ctx.Project
                    let symbolAssemblyFilename = symbolUse.Symbol.Assembly.SimpleName
                    let filteredProjects = 
                        allProjects
                        |> Seq.filter (fun proj ->
                                            let projectOutputfilename = getProjectOutputFilename config proj 
                                            //filter out the current project as this will be included with current dirty source file
                                            if currentContextProjectFilename = projectOutputfilename then false
                                            //if the symbol and project and symbol match include it
                                            elif projectOutputfilename = symbolAssemblyFilename then true
                                            else
                                                let projectsRefSymbol =
                                                    proj.GetReferencedItems(config)
                                                    |> Seq.cast<DotNetProject>
                                                    |> Seq.tryFind (fun rp -> let projectOutput = getProjectOutputFilename config rp
                                                                              projectOutput = symbolAssemblyFilename)
                                                projectsRefSymbol.IsSome )

                    filteredProjects
                    |> Seq.map (fun p -> p.FileName.ToString()) 
                    |> Seq.toList
                with _ -> []

            let! symbolrefs =
                langServ.GetUsesOfSymbolInProject(ctx.Project.FileName.ToString(), editor.FileName.ToString(), editor.Text, symbolUse.Symbol, dependentProjects)

            let distinctRefs = 
                symbolrefs
                |> Array.map (Symbols.getOffsetsTrimmed lastIdent)
                |> Seq.distinct

            for (filename, startOffset, endOffset) in distinctRefs do
                let sr = SearchResult (FileProvider (filename), startOffset, endOffset-startOffset)
                monitor.ReportResult sr

            }
        let onComplete _ = monitor.Dispose()
        Async.StartWithContinuations(findAsync, onComplete, onComplete, onComplete)

type CurrentRefactoringOperationsHandler() =
    inherit CommandHandler()

    let formatFileName (fileName:string) =
        if fileName |> String.isNullOrEmpty then fileName else
        let fileParts =
            fileName
            |> String.split [|Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar|]

        match fileParts with
        | [||] -> fileName
        | xs -> xs |> Array.last

    let tryGetValidDoc() =
        let doc = IdeApp.Workbench.ActiveDocument
        if doc = null || doc.FileName = FilePath.Null || doc.ParsedDocument = null then None
        else Some doc

    override x.Run () =
        base.Run ()

    override x.Run (data) =
        let del =  data :?> Action
        if del <> null
        then del.Invoke ()
    
    override x.Update (ci:CommandInfo) =
        base.Update (ci)
          
    override x.Update (ainfo:CommandArrayInfo) =
        match tryGetValidDoc() with
        | None -> ()
        | Some doc ->
            if not (MDLanguageService.SupportedFileName (doc.FileName.ToString())) then ()
            else
                match doc.ParsedDocument.TryGetAst () with
                | None -> ()
                | Some ast ->
                    match Refactoring.getSymbolAndLineInfoAtCaret ast doc.Editor with
                    | (_line, col, lineTxt), Some symbolUse ->
                        let ciset = new CommandInfoSet (Text = GettextCatalog.GetString ("Refactor"))
            
                        //last ident part of surrent symbol
                        let lastIdent = Symbols.lastIdent col lineTxt
            
                        //rename refactoring
                        let canRename = Refactoring.canRename symbolUse doc.Editor.FileName doc.Project.ParentSolution
                        if canRename then
                            let commandInfo = IdeApp.CommandService.GetCommandInfo (Commands.EditCommands.Rename)
                            commandInfo.Enabled <- true
                            ciset.CommandInfos.Add (commandInfo, Action(fun _ -> (Refactoring.rename (doc.Editor, doc, lastIdent, symbolUse))))
            
                        // goto to declaration
                        if Refactoring.canJump symbolUse doc.Editor.FileName doc.Project.ParentSolution then
                            let locations = Symbols.getLocationFromSymbolUse symbolUse
                            match locations with
                            | [] -> ()
                            | [_location] ->
                                let commandInfo = IdeApp.CommandService.GetCommandInfo (RefactoryCommands.GotoDeclaration)
                                commandInfo.Enabled <- true
                                ainfo.Add (commandInfo, Action (fun _ -> Refactoring.jumpToDeclaration (doc.Editor, doc, symbolUse) ))
                            | locations ->
                                let declSet = CommandInfoSet (Text = GettextCatalog.GetString ("_Go to Declaration"))
                                for location in locations do
                                    let commandText = String.Format (GettextCatalog.GetString ("{0}, Line {1}"), formatFileName location.FileName, location.StartLine)
                                    declSet.CommandInfos.Add (commandText, Action (fun () -> Refactoring.jumpTo (doc.Editor, doc, symbolUse.Symbol, location)))
                                    |> ignore
                                ainfo.Add (declSet)
                        
                        //goto base
                        if Refactoring.canGotoBase symbolUse then
                            let baseSymbol = Refactoring.getBaseSymbol symbolUse
                            match baseSymbol with
                            | Some bs ->
                                let symbol = 
                                    match bs with
                                    | Refactoring.BaseSymbol.Member m -> m :> FSharpSymbol
                                    | Refactoring.BaseSymbol.Type t -> t :> FSharpSymbol
                                let _baseIdent = symbol.DisplayName
                                let locations = Symbols.getLocationFromSymbol symbol
                                match locations with
                                | [] -> ()
                                | [location] -> 
                                    let description = GettextCatalog.GetString ("Go to _Base Symbol")
                                    ainfo.Add (description, Action (fun () -> Refactoring.jumpTo (doc.Editor, doc, symbol, location)))
                                    |> ignore
                                    
                                | locations ->
                                    let declSet = CommandInfoSet (Text = GettextCatalog.GetString ("Go to _Base Symbol"))
                                    for location in locations do
                                        let commandText = String.Format (GettextCatalog.GetString ("{0}, Line {1}"), formatFileName location.FileName, location.StartLine)
                                        declSet.CommandInfos.Add (commandText, Action (fun () -> Refactoring.jumpTo (doc.Editor, doc, symbol, location)))
                                        |> ignore
                                    ainfo.Add (declSet)
                            | _ -> ()
 
                        //find references
                        // renamable indicates the source symbol is internal to the project
                        if canRename then
                            let command = IdeApp.CommandService.GetCommandInfo (RefactoryCommands.FindReferences)
                            command.Enabled <- true
                            ainfo.Add (command, Action (fun () -> Refactoring.findReferences (doc.Editor, doc, symbolUse, lastIdent)))
                            //this one finds all overloads of a given symbol
                            //All that needs to happen here is to pass all dependent project infos
                            //ainfo.Add (IdeApp.CommandService.GetCommandInfo (RefactoryCommands.FindAllReferences), Action (fun () -> Refactoring.FindRefs (sym)))

                        //TODO: find derived symbols, find overloads, find extension methods, find type extensions
            
                        if ciset.CommandInfos.Count > 0 then
                            ainfo.Add (ciset, null)
                    | _ -> ()

type RenameHandler() =
    inherit CommandHandler()

    override x.Update (ci:CommandInfo) =
        let doc = IdeApp.Workbench.ActiveDocument
        let editor = doc.Editor
        //skip if theres no editor or filename
        if editor = null || editor.FileName = FilePath.Null
        then ci.Bypass <- false
        else
            if not (MDLanguageService.SupportedFilePath editor.FileName) then ci.Bypass <- true
            else
                match doc.ParsedDocument.TryGetAst() with
                | Some ast ->
                    match Refactoring.getSymbolAndLineInfoAtCaret ast editor with
                    //set bypass is we cant rename
                    | _lineinfo, Some sym when not (Refactoring.canRename sym editor.FileName doc.Project.ParentSolution) ->
                        ci.Bypass <- true
                    | _lineinfo, _symbol -> ()
                //disable for no ast
                | None ->
                    ci.Bypass <- true

    member x.UpdateCommandInfo(ci:CommandInfo) = x.Update(ci)
        
    override x.Run (_data) =
        let doc = IdeApp.Workbench.ActiveDocument
        if doc <> null || doc.FileName <> FilePath.Null || not (MDLanguageService.SupportedFilePath doc.FileName) then
            x.Run (doc.Editor, doc)

    member x.Run ( editor:TextEditor, ctx:DocumentContext) =
        if MDLanguageService.SupportedFilePath editor.FileName then 
            match ctx.ParsedDocument.TryGetAst() with
            | Some ast ->
                match Refactoring.getSymbolAndLineInfoAtCaret ast editor with
                //Is this a double check, i.e. isnt update checking can rename?
                | (_line, col, lineTxt), Some sym when Refactoring.canRename sym editor.FileName ctx.Project.ParentSolution ->
                    let lastIdent = Symbols.lastIdent col lineTxt
                    Refactoring.rename (editor, ctx, lastIdent, sym)
                | _ -> ()
            | _ -> ()


open ExtCore
type GotoDeclarationHandler() =
    inherit CommandHandler()

    member x.UpdateCommandInfo(ci:CommandInfo) =
        x.Update(ci)

    override x.Update (ci:CommandInfo) =
        let doc = IdeApp.Workbench.ActiveDocument
        let editor = doc.Editor
        //skip if theres no editor or filename
        if editor = null || editor.FileName = FilePath.Null then ci.Bypass <- true
        elif not (MDLanguageService.SupportedFilePath editor.FileName) then ci.Bypass <- true
        else
            match doc.ParsedDocument.TryGetAst() with
            | Some ast ->
                match Refactoring.getSymbolAndLineInfoAtCaret ast editor with
                //set bypass as we cant jump
                | _lineinfo, Some sym when not (Refactoring.canJump sym editor.FileName doc.Project.ParentSolution) ->
                    ci.Bypass <- true
                | _lineinfo, _symbol -> ()
            //disable for no ast
            | None -> ci.Bypass <- true

    override x.Run (_data) =
        let doc = IdeApp.Workbench.ActiveDocument
        if doc <> null || doc.FileName <> FilePath.Null || not (MDLanguageService.SupportedFilePath doc.FileName) then
            x.Run(doc.Editor, doc)
            
    member x.Run(editor, context:DocumentContext) =
        if MDLanguageService.SupportedFileName (editor.FileName.ToString()) then
            match context.ParsedDocument.TryGetAst() with
            | Some ast ->
                match Refactoring.getSymbolAndLineInfoAtCaret ast editor with
                | (_line, _col, _lineTxt), Some symbolUse when Refactoring.canJump symbolUse editor.FileName context.Project.ParentSolution ->
                        //let lastIdent = Symbols.lastIdent col lineTxt
                        Refactoring.jumpToDeclaration (editor, context, symbolUse)
                | _ -> ()
            | _ -> ()

type FSharpCommandsTextEditorExtension () =
    inherit Editor.Extension.TextEditorExtension ()

    override x.IsValidInContext (context) =
        context.Name <> null && MDLanguageService.SupportedFileName context.Name

    [<CommandUpdateHandler(RefactoryCommands.GotoDeclaration)>]
    member x.GotoDeclarationCommand_Update(ci:CommandInfo) =
        GotoDeclarationHandler().UpdateCommandInfo (ci)
    
    [<CommandHandler (RefactoryCommands.GotoDeclaration)>]
    member x.GotoDeclarationCommand () =
        GotoDeclarationHandler().Run(x.Editor, x.DocumentContext)
        
    [<CommandUpdateHandler(Commands.EditCommands.Rename)>]
    member x.RenameCommand_Update(ci:CommandInfo) =
        RenameHandler().UpdateCommandInfo (ci)
    
    [<CommandHandler (Commands.EditCommands.Rename)>]
    member x.RenameCommand () =
        RenameHandler().Run (x.Editor, x.DocumentContext)