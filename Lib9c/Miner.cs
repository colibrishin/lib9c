using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Libplanet;
using Libplanet.Action;
using Libplanet.Blockchain;
using Libplanet.Blocks;
using Libplanet.Crypto;
using Libplanet.Net;
using Libplanet.Tx;
using Serilog;
using NCAction = Libplanet.Action.PolymorphicAction<Nekoyume.Action.ActionBase>;

namespace Nekoyume.BlockChain
{
    public class Miner<T>
        where T : IAction, new()
    {
        private readonly BlockChain<T> _chain;
        private readonly Swarm<T> _swarm;
        private readonly PrivateKey _privateKey;
        // TODO we must justify it.
        private readonly ImmutableHashSet<Address> _bannedAccounts = new[]
        {
            new Address("de96aa7702a7a1fd18ee0f84a5a0c7a2c28ec840"),
            new Address("153281c93274bEB9726A03C33d3F19a8D78ad805"),
            new Address("7035AA8B7F9fB5db026fb843CbB21C03dd278502"),
            new Address("52393Ea89DF0E58152cbFE673d415159aa7B9dBd"),
            new Address("2D1Db6dBF1a013D648Efd16d85B4079dCF88B4CC"),
            new Address("dE30E00917B583305f14aD21Eafc70f1b183b779"),
            new Address("B892052f1E10bf700143dd9bEcd81E31CD7f7095"),

            new Address("C0a90FC489738A1153F793A3272A91913aF3956b"),
            new Address("b8D7bD4394980dcc2579019C39bA6b41cb6424E1"),
            new Address("555221D1CEA826C55929b8A559CA929574f7C6B3"),
            new Address("B892052f1E10bf700143dd9bEcd81E31CD7f7095"),
        }.ToImmutableHashSet();

        public Address Address => _privateKey.ToAddress();

        /// <summary>
        /// Propose and broadcast block without appending.
        /// </summary>
        /// <param name="lastCommit">List of lastCommits to be included.</param>
        /// <returns></returns>
        public Block<T> ProposeBlock(BlockCommit? lastCommit)
        {
            var txs = new HashSet<Transaction<T>>();
            var invalidTxs = txs;

            Block<T> block = null;
            try
            {
                IEnumerable<Transaction<T>> bannedTxs = _chain.GetStagedTransactionIds()
                    .Select(txId => _chain.GetTransaction(txId))
                    .Where(tx => _bannedAccounts.Contains(tx.Signer));
                foreach (Transaction<T> tx in bannedTxs)
                {
                    _chain.UnstageTransaction(tx);
                }

                block = _chain.ProposeBlock(
                    _privateKey,
                    DateTimeOffset.UtcNow,
                    lastCommit: lastCommit);

                if (_swarm is Swarm<T> s && s.Running)
                {
                    s.BroadcastBlock(block);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Debug("Mining was canceled due to change of tip.");
            }
            catch (InvalidTxException invalidTxException)
            {
                var invalidTx = _chain.GetTransaction(invalidTxException.TxId);

                Log.Debug($"Tx[{invalidTxException.TxId}] is invalid. mark to unstage.");
                invalidTxs.Add(invalidTx);
            }
            catch (UnexpectedlyTerminatedActionException actionException)
            {
                if (actionException.TxId is TxId txId)
                {
                    Log.Debug(
                        $"Tx[{actionException.TxId}]'s action is invalid. mark to unstage. {actionException}");
                    invalidTxs.Add(_chain.GetTransaction(txId));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"exception was thrown. {ex}");
            }
            finally
            {
#pragma warning disable LAA1002
                foreach (var invalidTx in invalidTxs)
#pragma warning restore LAA1002
                {
                    _chain.UnstageTransaction(invalidTx);
                }

            }

            return block;
        }

        public Miner(
            BlockChain<T> chain,
            Swarm<T> swarm,
            PrivateKey privateKey
        )
        {
            _chain = chain ?? throw new ArgumentNullException(nameof(chain));
            _swarm = swarm;
            _privateKey = privateKey;
        }
    }
}
