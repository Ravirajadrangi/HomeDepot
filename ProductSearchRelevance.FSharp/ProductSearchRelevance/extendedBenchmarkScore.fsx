﻿#I @"../packages/"

#r "Accord/lib/net45/Accord.dll"
#r "FSharp.Data/lib/net40/FSharp.Data.dll"
#r "Accord.Math/lib/net45/Accord.Math.dll"
#r "Accord.Statistics/lib/net45/Accord.Statistics.dll"
#r "FSharp.Collections.ParallelSeq/lib/net40/FSharp.Collections.ParallelSeq.dll"
#r "StemmersNet/lib/net20/StemmersNet.dll"
#r "alglibnet2/lib/alglibnet2.dll"
#r "FuzzyString/lib/FuzzyString.dll"
#load "CsvData.fs"
#load "StringUtils.fs"
#load "Core.fs"

open System
open FSharp.Collections.ParallelSeq
open StringUtils
open Accord.Statistics.Models.Regression.Linear
open HomeDepot.Core

printfn "Building Brand Name set..."
let brands =
    CsvData.attributes.Rows
    |> Seq.where (fun r -> r.Name = "MFG Brand Name")
    |> Seq.map (fun r -> r.Value.ToLowerInvariant().Replace(" & ", " and ") |> sanitize)
    |> Set.ofSeq

let isMatch = startsWithMatch
let features attrSelector productBrand (sample:CsvData.Sample) =
    let queryWords = sample.Query |> splitOnSpaces |> Array.distinct
    let titleMatches = queryWords |> Array.filter (isMatch sample.Title)
    let descMatches = queryWords |> Array.filter (isMatch sample.Description)
    let attributes = prepareText <| attrSelector sample.ProductId
    let attrMatches = queryWords |> Array.filter (isMatch attributes)
    let wordMatchCount =
        queryWords
        |> Seq.filter (fun w -> Seq.concat [titleMatches; descMatches; attrMatches] |> Seq.contains w)
        |> Seq.length
    let brandNameMatch =
        match productBrand with // does query contain product brand?
        | Some bn -> if queryWords |> Array.exists (containedIn bn) then 1 else 0
        | None ->
          // does query contain any brand name?
          let searchedBrand = brands |> Set.filter (containedIn sample.Query) |> Seq.tryHead
          match searchedBrand with // is query brand name in product title?
          | Some b -> if b |> containedIn sample.Title then 1 else -1
          | None -> 0
    // feature array
    [| float queryWords.Length
       float sample.Query.Length
       float sample.Title.Length
       float wordMatchCount
       float titleMatches.Length
       float titleMatches.Length / float queryWords.Length
       float descMatches.Length
       float descMatches.Length / float queryWords.Length
       float attrMatches.Length
       float brandNameMatch |]

let prepSample (sample:CsvData.Sample) =
    { sample with
        Title = prepareText sample.Title
        Description = prepareText sample.Description
        Query = prepareText sample.Query }

let getAttr attribMap productId =
    match attribMap |> Map.tryFind productId with
    | Some a ->
      let getAttrStr name (value:string) =
          match value.ToLowerInvariant() with
          | "yes" -> name // if true attrib, include attrib name
          | "no"  -> String.Empty
          | _     -> value
      a |> Seq.map (fun (_, name, value) -> getAttrStr name value) |> String.concat " "
    | None -> String.Empty

let brandName attribMap uid =
    match attribMap |> Map.tryFind uid with
    | Some a ->
      let brand = a |> Seq.tryFind (fun (_, name, _) -> name = "MFG Brand Name")
      brand |> Option.map (fun (_, _, value) -> value)
    | None -> None

let getFeatures attribs attribMap sample =
    sample |> prepSample |> features attribs (brandName attribMap sample.ProductId)

let extractFeatures featureExtractor = 
    PSeq.ordered
    >> PSeq.map featureExtractor
    >> PSeq.toArray

let rfLearn (examples:Example array) attribMap =
  let samples, trainOutput = Array.unzip examples

  printfn "Extracting training features..."
  let attribs = getAttr attribMap
  let getFeatures' = getFeatures attribs attribMap
  let trainInput = samples |> extractFeatures getFeatures'
  // NOTE: ALGLIB wants prediction variable at end of input array
  let trainInputOutput =
      Seq.zip trainInput trainOutput
      |> Seq.map (fun (i,o) -> Array.append i [|o|])
      |> array2D

  printfn "Random Decision Forest regression..."
  let trees = 75
  let treeTrainSize = 0.15
  let featureCount = trainInput.[0].Length
  let _info, forest, forestReport =
      alglib.dfbuildrandomdecisionforest(trainInputOutput, trainInput.Length, featureCount, 1, trees, treeTrainSize)
  printfn "RDF RMS Error: %f; Out-of-bag RMS Error: %f" forestReport.rmserror forestReport.oobrmserror

  let predict (sample:CsvData.Sample) : Quality =
      let features = getFeatures' sample
      let mutable result : float [] = [||]
      alglib.dfprocess(forest, features, &result)
      result.[0]
  predict

let rfQuality = evaluate rfLearn
submission rfLearn
//0.48737 = kaggle rsme; RDF RMS Error: 0.430396; Out-of-bag RMS Error: 0.477678
//0.48740 = kaggle rsme; RDF RMS Error: 0.430214; Out-of-bag RMS Error: 0.477583
//?.????? = kaggle rsme; RDF RMS Error: 0.430041; Out-of-bag RMS Error: 0.477391

let linLearn (examples:Example array) attribMap =
  let samples, trainOutput = Array.unzip examples

  printfn "Extracting training features..."
  let attribs = getAttr attribMap
  let getFeatures' = getFeatures attribs attribMap
  let trainInput : float [] [] = extractFeatures getFeatures' samples

  printfn "Multiple linear regression..."
  let featureCount = trainInput.[0].Length
  let target = MultipleLinearRegression(featureCount, true)
  let sumOfSquaredErrors = target.Regress(trainInput, trainOutput)
  let observationCount = float trainInput.Length
  let sme = sumOfSquaredErrors / observationCount
  let rsme = sqrt(sme)
  printfn "Linear regression RSME: %f" rsme

  let predict (sample:CsvData.Sample) : Quality =
      let features = getFeatures' sample 
      target.Compute(features)
  predict

let linQuality = evaluate linLearn
////0.48754 - string handling tweaks
////0.48835 - sanitize text input
////0.48917 - stem all words for comparison
////0.48940 - added title & desc length feature
////0.49059 - added desc word match ratio
////0.49080 - added title word match ratio
////0.49279 - added query length feature
////0.49359 - better brand matching
////0.49409 - attributes + some brand matching
////0.49665 - stemmed
////0.50783 - kaggle reported from stemmed
////0.5063 - string contains

//let writeResults name rows =
//    let outputPath = __SOURCE_DIRECTORY__ + sprintf "../../data/%s_submission_FSharp.csv" name
//    File.WriteAllLines(outputPath, "id,relevance" :: rows)  
//
//let inline bracket n = Math.Max(1., Math.Min(3., n)) // ensure output between 1..3
//
//let linRegSubmission = 
//    Seq.zip test.Rows testOutput
//    |> PSeq.ordered
//    |> PSeq.map (fun (r,o) -> sprintf "%A,%A" r.Id o)
//    |> PSeq.toList
//    |> writeResults "linear"
