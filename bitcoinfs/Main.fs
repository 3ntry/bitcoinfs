﻿(*** hide ***)
(* Copyright 2015 Hanh Huynh Huu

This file is part of F# Bitcoin.

F# Bitcoin is free software; you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation version 3 of the License.

F# Bitcoin is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
You should have received a copy of the GNU General Public License
along with F# Bitcoin; if not, write to the Free Software
Foundation, Inc., 51 Franklin St, Fifth Floor, Boston, MA  02110-1301  USA
*)

(**
# Main
*)
module Main

(*** hide ***)
open System
open System.IO
open System.Net
open System.Threading
open System.Collections.Generic
open System.Data.Linq
open System.Data.SQLite
open System.Linq
open System.Reactive
open System.Reactive.Linq
open System.Reactive.Subjects
open log4net
open Org.BouncyCastle.Crypto
open Org.BouncyCastle.Utilities.Encoders
open FSharpx
open FSharpx.Collections
open FSharpx.Choice
open Protocol
open Peer
open Tracker
open LevelDB
open Checks
open Blockchain
open Wallet
open Db
open Script

(* The following stuff is for skipping validation during the import of a bootstrap file and for debugging purposes *)
let skipScript (script: byte[]) = 
    script.Length = 25 && script.[0] = OP_DUP && script.[1] = OP_HASH160 && script.[24] = OP_CHECKSIG

let processUTXOFast (utxoAccessor: IUTXOAccessor) (height) (tx: Tx) (i: int) =
    tx.TxIns |> Seq.iteri (fun iTxIn txIn ->
        if i <> 0 then
            let scriptRuntime = new ScriptRuntime(Script.computeTxHash tx iTxIn)
            utxoAccessor.GetUTXO txIn.PrevOutPoint |> Option.iter(fun txOut ->
                let inScript = txIn.Script
                let script = txOut.TxOut.Script
                (*
                if not (scriptRuntime.Verify(inScript, script)) then
                    logger.ErrorF "Script failed on tx %A for input %d" tx iTxIn
                *)
                utxoAccessor.DeleteUTXO txIn.PrevOutPoint
                )
        )
    tx.TxOuts |> Seq.iteri (fun iTxOut txOut ->
        let outpoint = new OutPoint(tx.Hash, iTxOut)
        utxoAccessor.AddUTXO (outpoint, UTXO(txOut, 0))
        )

(**
*)
let readBootstrapFast (firstBlock: int) (stream: Stream) =
    use reader = new BinaryReader(stream)
    let mutable i = firstBlock
    let mutable tip: byte[] = null
    while(stream.Position <> stream.Length) do
        if i % 10000 = 0 then
            logger.InfoF "%d" i
        let magic = reader.ReadInt32()
        let length = reader.ReadInt32()
        let block = ParseByteArray (reader.ReadBytes(length)) Block.Parse
        block.Header.Height <- i
        block.Header.IsMain <- true
        let prevBH = Db.readHeader block.Header.PrevHash
        if prevBH.Hash <> zeroHash 
        then 
            prevBH.NextHash <- block.Header.Hash
            Db.writeHeaders prevBH
        block.Txs |> Seq.iteri (fun idx tx -> processUTXOFast utxoAccessor block.Header.Height tx idx)
        Db.writeHeaders block.Header
        tip <- block.Header.Hash
        i <- i + 1
    logger.InfoF "Last block %d" i
    Db.writeTip tip

let writeBootstrap (firstBlock: int) (lastBlock: int) (stream: Stream) =
    use writer = new BinaryWriter(stream)
    for i in firstBlock..lastBlock do
        logger.InfoF "Writing block #%d" i
        let bh = Db.getHeaderByHeight i
        let block = Db.loadBlock bh
        writer.Write(magic)
        writer.Write(Db.getBlockSize bh)
        writer.Write(block.ToByteArray())

(*** hide ***)
let verifySingleTx (tx: Tx) (iTxIn: int) (outScript: byte[]) = 
    let scriptRuntime = new ScriptRuntime(Script.computeTxHash tx iTxIn)
    let txIn = tx.TxIns.[iTxIn]
    let inScript = txIn.Script
    scriptRuntime.Verify(inScript, outScript)

let decodeTx (s: string) =
    let hex = Hex.Decode(s)
    use ms = new MemoryStream(hex)
    use reader = new BinaryReader(ms)
    Tx.Parse(reader)
    
let readBootstrap (firstBlock: int) (stream: Stream) =
    use reader = new BinaryReader(stream)
    let mutable i = firstBlock
    while(stream.Position <> stream.Length) do
        if i % 10000 = 0 then
            logger.DebugF "%d" i
        let magic = reader.ReadInt32()
        let length = reader.ReadInt32()
        let block = ParseByteArray (reader.ReadBytes(length)) Block.Parse
        block.Header.Height <- i
        updateBlockUTXO utxoAccessor block |> ignore
        i <- i + 1
    logger.DebugF "Last block %d" i

(**
The main function initializes the application and waits forever
*)
[<EntryPoint>]
let main argv = 
    Config.BasicConfigurator.Configure() |> ignore
    // RPC.startRPC()

    // Db.scanUTXO()
    // Write a bootstrap file from saved blocks

(*
    use stream = new FileStream("D:/bootstrap-nnn.dat", FileMode.CreateNew, FileAccess.Write)
    writeBootstrap 341001 342000 stream
*)

(*
    // Import a couple of bootstrap dat files
    use stream = new FileStream("J:/bootstrap-295000.dat", FileMode.Open, FileAccess.Read)
    readBootstrapFast 0 stream
    use stream = new FileStream("J:/bootstrap-332702.dat", FileMode.Open, FileAccess.Read)
    readBootstrapFast 295001 stream
    use stream = new FileStream("J:/bootstrap-341000.dat", FileMode.Open, FileAccess.Read)
    readBootstrapFast 332703 stream
*)

    Peer.initPeers()
    
    Tracker.startTracker()
    Tracker.startServer()
    Mempool.startMempool()
    Blockchain.blockchainStart()
(*
*)
    (* Manually import my own local node
    let myNode = new IPEndPoint(IPAddress.Loopback, 8333)
    trackerIncoming.OnNext(TrackerCommand.Connect myNode)
    *)
(*
*)
    trackerIncoming.OnNext(TrackerCommand.GetPeers)
    Thread.Sleep(-1)
    0 // return an integer exit code
