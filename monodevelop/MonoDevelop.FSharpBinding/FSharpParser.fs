﻿namespace MonoDevelop.FSharp

open System
open System.Diagnostics
open System.IO
open MonoDevelop.Ide
open MonoDevelop.Ide.Gui
open MonoDevelop.Core
open MonoDevelop.Ide.TypeSystem
open ICSharpCode.NRefactory.TypeSystem
open Microsoft.FSharp.Compiler
open FSharp.CompilerBinding

type FSharpParsedDocument(fileName) = 
     inherit DefaultParsedDocument(fileName)
  
// An instance of this type is created by MonoDevelop (as defined in the .xml for the AddIn) 
type FSharpParser() =
  inherit TypeSystemParser()
  do Debug.WriteLine("Parsing: Creating FSharpParser")
        
  /// Holds the previous errors reported by a file. 
  let activeResults = System.Collections.Generic.Dictionary<string,Error list>()

  /// Holds the previous content used to generate the previous errors. An entry is only present if we have 
  /// scheduled a new ReparseDocument() to update the errors.
  let activeRequests = System.Collections.Generic.Dictionary<string,string>()
  let mutable typedParsedResult = ParseAndCheckResults.Empty

    /// Format errors for the given line (if there are multiple, we collapse them into a single one)
  let formatError (error:ErrorInfo) =
      // Single error for this line
      let typ = if error.Severity = Severity.Error then ErrorType.Error else ErrorType.Warning
      new Error(typ, error.Message, DomRegion(error.StartLineAlternate, error.StartColumn + 1, error.EndLineAlternate, error.EndColumn + 1))
  
  /// To be called from the language service mailbox processor (on a 
  /// GUI thread!) when new errors are reported for the specified file
  let makeErrors(currentErrors:ErrorInfo[]) = 
    [ for error in currentErrors do
          yield formatError error ]

  override x.Parse(storeAst:bool, fileName:string, content:System.IO.TextReader, proj:MonoDevelop.Projects.Project) =
    if fileName = null || proj = null ||  not (CompilerArguments.supportedExtension(Path.GetExtension(fileName))) then null else

    let fileContent = content.ReadToEnd()

    Debug.WriteLine("[Thread {0}] Parsing: Update in FSharpParser.Parse to file {1}, hash {2}", System.Threading.Thread.CurrentThread.ManagedThreadId, fileName, hash fileContent)

    let doc = new FSharpParsedDocument(fileName)
    doc.Flags <- doc.Flags ||| ParsedDocumentFlags.NonSerializable

    // Check if this is a reparse.
    match activeRequests.TryGetValue(fileName) with 
    | true, content when content = fileContent ->
        activeRequests.Remove(fileName) |> ignore

    | _ ->
  
      Debug.WriteLine("[Thread {0}]: TriggerParse file {1}, hash {2}", System.Threading.Thread.CurrentThread.ManagedThreadId, fileName, hash fileContent)
      // Trigger a parse/typecheck in the background. After the parse/typecheck is completed, request another parse to report the errors.
      //
      // Trigger parsing in the language service 
      let filePathOpt = 
          // TriggerParse will work only for full paths
          if IO.Path.IsPathRooted(fileName) then 
              Some(fileName)
          else 
             let doc = IdeApp.Workbench.ActiveDocument 
             if doc <> null then 
                 let file = doc.FileName.ToString()
                 if file = "" then None else Some file
             else None

      match filePathOpt with 
      | None -> ()
      | Some filePath -> 
        let config = IdeApp.Workspace.ActiveConfiguration
        if config <> null then 
          // Keep a record that we have an inflight check of this going on
          activeRequests.[fileName] <- fileContent
          let proj = proj :?> MonoDevelop.Projects.DotNetProject
          let files = CompilerArguments.getSourceFiles(proj.Items) |> Array.ofList
          let args = CompilerArguments.getArgumentsFromProject(proj, config)
          let framework = CompilerArguments.getTargetFramework(proj.TargetFramework.Id)

          async { 
             let! results = MDLanguageService.Instance.ParseAndCheckFileInProject(proj.FileName.ToString(), filePath, fileContent, files, args, framework)
             match results.GetErrors() with 
             | None -> ()
             | Some errors -> 
                 try 
                     Debug.WriteLine("[Thread {0}]: Callback after parsing, file {1}, hash {2}", System.Threading.Thread.CurrentThread.ManagedThreadId, fileName, hash fileContent)

                     // Keep the result until we reparse
                     activeResults.[fileName] <- makeErrors errors
                     typedParsedResult <- results
                     // Schedule a reparse to actually update the errors, checking first if this is still the active document
                     let doc = IdeApp.Workbench.ActiveDocument
                     if doc <> null && doc.FileName.FullPath.ToString() = fileName then 
                        Debug.WriteLine("[Thread {0}]: Parsing: Requesting re-parse of file {1}, hash {2}",System.Threading.Thread.CurrentThread.ManagedThreadId,fileName, hash fileContent)
                        doc.ReparseDocument()
                 with _ -> ()
           } |> Async.StartImmediate

    if activeResults.ContainsKey(fileName) then
        for er in activeResults.[fileName] do 
            doc.Errors.Add(er)  

        //Set code folding regions
        //GetNavigationItems may throw in some situations
        try
          let regions =
            let processDecl (decl:SourceCodeServices.DeclarationItem) =
              let m = decl.Range
              FoldingRegion(decl.Name, DomRegion(m.StartLine, m.StartColumn+1, m.EndLine, m.EndColumn+1))
                  
            seq{for toplevel in typedParsedResult.GetNavigationItems() do
                  yield processDecl toplevel.Declaration
                  for next in toplevel.Nested do yield processDecl next}
          doc.Add(regions)
        with _ -> Debug.Assert(false, "couldn't update navigation items, ignoring")  
        
        //also store the AST of active results if applicable                
        if storeAst then
            doc.Ast <- typedParsedResult
    
    doc.LastWriteTimeUtc <- (try File.GetLastWriteTimeUtc (fileName) with _ -> DateTime.UtcNow) 
    doc :> ParsedDocument
