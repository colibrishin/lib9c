namespace Lib9c.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Libplanet;
    using Libplanet.Action;
    using Libplanet.Assets;
    using Libplanet.Blockchain;
    using Libplanet.Blockchain.Policies;
    using Libplanet.Blocks;
    using Libplanet.Consensus;
    using Libplanet.Crypto;
    using Libplanet.Store;
    using Libplanet.Store.Trie;
    using Libplanet.Tx;
    using Nekoyume;
    using Nekoyume.Action;
    using Nekoyume.BlockChain;
    using Nekoyume.BlockChain.Policy;
    using Nekoyume.Model;
    using Nekoyume.Model.State;
    using Serilog.Core;
    using Xunit;

    public class BlockPolicyTest
    {
        private readonly PrivateKey _privateKey;
        private readonly Currency _currency;

        public BlockPolicyTest()
        {
            _privateKey = new PrivateKey();
#pragma warning disable CS0618
            // Use of obsolete method Currency.Legacy(): https://github.com/planetarium/lib9c/discussions/1319
            _currency = Currency.Legacy("NCG", 2, _privateKey.ToAddress());
#pragma warning restore CS0618
        }

        [Fact]
        public void ValidateNextBlockTx()
        {
            var adminPrivateKey = new PrivateKey();
            var adminAddress = adminPrivateKey.ToAddress();

            var blockPolicySource = new BlockPolicySource(Logger.None);
            IBlockPolicy<PolymorphicAction<ActionBase>> policy = blockPolicySource.GetPolicy(
                10_000, null, null, null, null, null, null, null);
            IStagePolicy<PolymorphicAction<ActionBase>> stagePolicy =
                new VolatileStagePolicy<PolymorphicAction<ActionBase>>();
            Block<PolymorphicAction<ActionBase>> genesis = MakeGenesisBlock(
                adminAddress,
                ImmutableHashSet.Create(adminAddress)
            );
            using var store = new DefaultStore(null);
            using var stateStore = new TrieStateStore(new DefaultKeyValueStore(null));
            var blockChain = new BlockChain<PolymorphicAction<ActionBase>>(
                policy,
                stagePolicy,
                store,
                stateStore,
                genesis,
                renderers: new[] { blockPolicySource.BlockRenderer }
            );
            Transaction<PolymorphicAction<ActionBase>> txByStranger =
                Transaction<PolymorphicAction<ActionBase>>.Create(
                    0,
                    new PrivateKey(),
                    genesis.Hash,
                    new PolymorphicAction<ActionBase>[] { }
                );

            // New private key which is not in activated addresses list is blocked.
            Assert.NotNull(policy.ValidateNextBlockTx(blockChain, txByStranger));

            var newActivatedPrivateKey = new PrivateKey();
            var newActivatedAddress = newActivatedPrivateKey.ToAddress();

            // Activate with admin account.
            blockChain.MakeTransaction(
                adminPrivateKey,
                new PolymorphicAction<ActionBase>[] { new AddActivatedAccount(newActivatedAddress) }
            );
            blockChain.Append(blockChain.ProposeBlock(adminPrivateKey));

            Transaction<PolymorphicAction<ActionBase>> txByNewActivated =
                Transaction<PolymorphicAction<ActionBase>>.Create(
                    0,
                    newActivatedPrivateKey,
                    genesis.Hash,
                    new PolymorphicAction<ActionBase>[] { }
                );

            // Test success because the key is activated.
            Assert.Null(policy.ValidateNextBlockTx(blockChain, txByNewActivated));

            var singleAction = new PolymorphicAction<ActionBase>[]
            {
                new DailyReward(),
            };
            var manyActions = new PolymorphicAction<ActionBase>[]
            {
                new DailyReward(),
                new DailyReward(),
            };
            Transaction<PolymorphicAction<ActionBase>> txWithSingleAction =
                Transaction<PolymorphicAction<ActionBase>>.Create(
                    0,
                    newActivatedPrivateKey,
                    genesis.Hash,
                    singleAction
                );
            Transaction<PolymorphicAction<ActionBase>> txWithManyActions =
                Transaction<PolymorphicAction<ActionBase>>.Create(
                    0,
                    newActivatedPrivateKey,
                    genesis.Hash,
                    manyActions
                );

            // Transaction with more than two actions is rejected.
            Assert.Null(policy.ValidateNextBlockTx(blockChain, txWithSingleAction));
            Assert.NotNull(policy.ValidateNextBlockTx(blockChain, txWithManyActions));
        }

        [Fact]
        public void MustNotIncludeBlockActionAtTransaction()
        {
            var adminPrivateKey = new PrivateKey();
            var adminAddress = adminPrivateKey.ToAddress();
            var authorizedMinerPrivateKey = new PrivateKey();

            (ActivationKey ak, PendingActivationState ps) = ActivationKey.Create(
                new PrivateKey(),
                new byte[] { 0x00, 0x01 }
            );

            var blockPolicySource = new BlockPolicySource(Logger.None);
            IBlockPolicy<PolymorphicAction<ActionBase>> policy = blockPolicySource.GetPolicy(
                10_000, null, null, null, null, null, null, null);
            IStagePolicy<PolymorphicAction<ActionBase>> stagePolicy =
                new VolatileStagePolicy<PolymorphicAction<ActionBase>>();
            Block<PolymorphicAction<ActionBase>> genesis = MakeGenesisBlock(
                adminAddress,
                ImmutableHashSet.Create(adminAddress),
                new AuthorizedMinersState(
                    new[] { authorizedMinerPrivateKey.ToAddress() },
                    5,
                    10
                ),
                pendingActivations: new[] { ps }
            );
            using var store = new DefaultStore(null);
            using var stateStore = new TrieStateStore(new DefaultKeyValueStore(null));
            var blockChain = new BlockChain<PolymorphicAction<ActionBase>>(
                policy,
                stagePolicy,
                store,
                stateStore,
                genesis,
                renderers: new[] { blockPolicySource.BlockRenderer }
            );

            Assert.Throws<MissingActionTypeException>(() =>
            {
                blockChain.MakeTransaction(
                    adminPrivateKey,
                    new PolymorphicAction<ActionBase>[] { new RewardGold() }
                );
            });
        }

        [Fact]
        public void EarnMiningGoldWhenSuccessMining()
        {
            var adminPrivateKey = new PrivateKey();
            var adminAddress = adminPrivateKey.ToAddress();
            var authorizedMinerPrivateKey = new PrivateKey();

            (ActivationKey ak, PendingActivationState ps) = ActivationKey.Create(
                new PrivateKey(),
                new byte[] { 0x00, 0x01 }
            );

            var blockPolicySource = new BlockPolicySource(Logger.None);
            IBlockPolicy<PolymorphicAction<ActionBase>> policy = blockPolicySource.GetPolicy(
                10_000, null, null, null, null, null, null, null);
            IStagePolicy<PolymorphicAction<ActionBase>> stagePolicy =
                new VolatileStagePolicy<PolymorphicAction<ActionBase>>();
            Block<PolymorphicAction<ActionBase>> genesis = MakeGenesisBlock(
                adminAddress,
                ImmutableHashSet.Create(adminAddress),
                new AuthorizedMinersState(
                    new[] { authorizedMinerPrivateKey.ToAddress() },
                    5,
                    10
                ),
                pendingActivations: new[] { ps }
            );

            using var store = new DefaultStore(null);
            using var stateStore = new TrieStateStore(new DefaultKeyValueStore(null));
            var blockChain = new BlockChain<PolymorphicAction<ActionBase>>(
                policy,
                stagePolicy,
                store,
                stateStore,
                genesis,
                renderers: new[] { blockPolicySource.BlockRenderer }
            );

            blockChain.MakeTransaction(
                adminPrivateKey,
                new PolymorphicAction<ActionBase>[] { new DailyReward(), }
            );

            blockChain.Append(blockChain.ProposeBlock(adminPrivateKey));
            FungibleAssetValue actualBalance = blockChain.GetBalance(adminAddress, _currency);
            FungibleAssetValue expectedBalance = new FungibleAssetValue(_currency, 10, 0);
            Assert.True(expectedBalance.Equals(actualBalance));
        }

        [Fact]
        public void ValidateNextBlockWithAuthorizedMinersPolicy()
        {
            var adminPrivateKey = new PrivateKey();
            var adminAddress = adminPrivateKey.ToAddress();
            var minerKeys = new[] { new PrivateKey(), new PrivateKey() };
            Address[] miners = minerKeys.Select(AddressExtensions.ToAddress).ToArray();
            var stranger = new PrivateKey();

            var blockPolicySource = new BlockPolicySource(Logger.None);
            IBlockPolicy<PolymorphicAction<ActionBase>> policy = blockPolicySource.GetPolicy(
                minimumDifficulty: 10_000,
                maxBlockBytesPolicy: null,
                minTransactionsPerBlockPolicy: null,
                maxTransactionsPerBlockPolicy: null,
                maxTransactionsPerSignerPerBlockPolicy: null,
                authorizedMinersPolicy: AuthorizedMinersPolicy
                    .Default
                    .Add(new SpannedSubPolicy<ImmutableHashSet<Address>>(
                        startIndex: 0,
                        endIndex: 4,
                        filter: index => index % 2 == 0,
                        value: miners.ToImmutableHashSet())),
                permissionedMinersPolicy: null,
                validatorsPolicy: ValidatorsPolicy.Default
                    .Add(new SpannedSubPolicy<ValidatorSet>(
                        0,
                        null,
                        null,
                        new ValidatorSet(new List<PublicKey>
                        {
                            adminPrivateKey.PublicKey,
                        }))));
            IStagePolicy<PolymorphicAction<ActionBase>> stagePolicy =
                new VolatileStagePolicy<PolymorphicAction<ActionBase>>();
            Block<PolymorphicAction<ActionBase>> genesis = MakeGenesisBlock(
                adminAddress,
                ImmutableHashSet<Address>.Empty);
            using var store = new DefaultStore(null);
            using var stateStore = new TrieStateStore(new DefaultKeyValueStore(null));
            var blockChain = new BlockChain<PolymorphicAction<ActionBase>>(
                policy,
                stagePolicy,
                store,
                stateStore,
                genesis,
                renderers: new[] { blockPolicySource.BlockRenderer }
            );

            BlockCommit GenerateBlockCommit(long height, BlockHash hash)
            {
                ImmutableArray<Vote> votes = ImmutableArray<Vote>.Empty
                    .Add(new VoteMetadata(
                        height, 0, hash, DateTimeOffset.UtcNow, adminPrivateKey.PublicKey, VoteFlag.PreCommit).Sign(adminPrivateKey));
                return new BlockCommit(height, 0, hash, votes);
            }

            blockChain.MakeTransaction(
                adminPrivateKey,
                new PolymorphicAction<ActionBase>[] { new DailyReward(), }
            );

            // Index 1. Anyone can mine.
            blockChain.Append(blockChain.ProposeBlock(stranger));

            // Index 2. Only authorized miner can mine.
            Assert.Throws<BlockPolicyViolationException>(() =>
            {
                blockChain.Append(blockChain.ProposeBlock(
                    stranger,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
            });
            // Old proof mining still works.
            blockChain.Append(blockChain.ProposeBlock(
                minerKeys[0],
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 3. Anyone can mine.
            blockChain.MakeTransaction(
                adminPrivateKey,
                new PolymorphicAction<ActionBase>[] { new DailyReward(), }
            );
            blockChain.Append(blockChain.ProposeBlock(
                stranger,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 4. Again, only authorized miner can mine.
            blockChain.MakeTransaction(
                adminPrivateKey,
                new PolymorphicAction<ActionBase>[] { new DailyReward(), }
            );
            Assert.Throws<BlockPolicyViolationException>(() =>
            {
                blockChain.Append(blockChain.ProposeBlock(
                    stranger,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
            });
            // No proof is required.
            blockChain.MakeTransaction(
                adminPrivateKey,
                new PolymorphicAction<ActionBase>[] { new DailyReward(), }
            );
            blockChain.Append(blockChain.ProposeBlock(
                minerKeys[1],
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 5, 6. Anyone can mine.
            blockChain.Append(blockChain.ProposeBlock(
                stranger,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
            blockChain.MakeTransaction(
                adminPrivateKey,
                new PolymorphicAction<ActionBase>[] { new DailyReward(), }
            );
            blockChain.Append(blockChain.ProposeBlock(
                stranger,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
        }

        [Fact]
        public void ValidateNextBlockWithManyTransactions()
        {
            var adminPrivateKey = new PrivateKey();
            var adminPublicKey = adminPrivateKey.PublicKey;
            var blockPolicySource = new BlockPolicySource(Logger.None);
            IBlockPolicy<PolymorphicAction<ActionBase>> policy = blockPolicySource.GetPolicy(
                minimumDifficulty: 10_000,
                maxBlockBytesPolicy: null,
                minTransactionsPerBlockPolicy: null,
                maxTransactionsPerBlockPolicy: MaxTransactionsPerBlockPolicy
                    .Default
                    .Add(new SpannedSubPolicy<int>(0, null, null, 10)),
                maxTransactionsPerSignerPerBlockPolicy: null,
                authorizedMinersPolicy: null,
                permissionedMinersPolicy: null,
                validatorsPolicy: ValidatorsPolicy.Default
                    .Add(new SpannedSubPolicy<ValidatorSet>(
                        0,
                        null,
                        null,
                        new ValidatorSet(new List<PublicKey>
                        {
                            adminPublicKey,
                        }))));
            IStagePolicy<PolymorphicAction<ActionBase>> stagePolicy =
                new VolatileStagePolicy<PolymorphicAction<ActionBase>>();
            Block<PolymorphicAction<ActionBase>> genesis =
                MakeGenesisBlock(adminPublicKey.ToAddress(), ImmutableHashSet<Address>.Empty);

            using var store = new DefaultStore(null);
            var stateStore = new TrieStateStore(new MemoryKeyValueStore());
            var blockChain = new BlockChain<PolymorphicAction<ActionBase>>(
                policy,
                stagePolicy,
                store,
                stateStore,
                genesis
            );

            int nonce = 0;
            List<Transaction<PolymorphicAction<ActionBase>>> GenerateTransactions(int count)
            {
                var list = new List<Transaction<PolymorphicAction<ActionBase>>>();
                for (int i = 0; i < count; i++)
                {
                    list.Add(Transaction<PolymorphicAction<ActionBase>>.Create(
                        nonce++,
                        adminPrivateKey,
                        genesis.Hash,
                        new PolymorphicAction<ActionBase>[] { }
                    ));
                }

                return list;
            }

            BlockCommit GenerateBlockCommit(long height, BlockHash hash)
            {
                ImmutableArray<Vote> votes = ImmutableArray<Vote>.Empty
                    .Add(new VoteMetadata(
                        height, 0, hash, DateTimeOffset.UtcNow, adminPublicKey, VoteFlag.PreCommit)
                        .Sign(adminPrivateKey));
                return new BlockCommit(height, 0, hash, votes);
            }

            Assert.Equal(1, blockChain.Count);
            var txs = GenerateTransactions(5).OrderBy(tx => tx.Id).ToList();
            Block<PolymorphicAction<ActionBase>> block1 = new BlockContent<PolymorphicAction<ActionBase>>(
                new BlockMetadata(
                    index: 1,
                    timestamp: DateTimeOffset.MinValue,
                    publicKey: adminPublicKey,
                    previousHash: blockChain.Tip.Hash,
                    txHash: BlockContent<PolymorphicAction<ActionBase>>.DeriveTxHash(txs),
                    lastCommit: null),
                transactions: txs).Propose().Evaluate(adminPrivateKey, blockChain);
            blockChain.Append(block1);
            Assert.Equal(2, blockChain.Count);
            Assert.True(blockChain.ContainsBlock(block1.Hash));
            txs = GenerateTransactions(10).OrderBy(tx => tx.Id).ToList();
            Block<PolymorphicAction<ActionBase>> block2 = new BlockContent<PolymorphicAction<ActionBase>>(
                new BlockMetadata(
                    index: 2,
                    timestamp: DateTimeOffset.MinValue,
                    publicKey: adminPublicKey,
                    previousHash: blockChain.Tip.Hash,
                    txHash: BlockContent<PolymorphicAction<ActionBase>>.DeriveTxHash(txs),
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)),
                transactions: txs).Propose().Evaluate(adminPrivateKey, blockChain);
            blockChain.Append(block2);
            Assert.Equal(3, blockChain.Count);
            Assert.True(blockChain.ContainsBlock(block2.Hash));
            txs = GenerateTransactions(11).OrderBy(tx => tx.Id).ToList();
            Block<PolymorphicAction<ActionBase>> block3 = new BlockContent<PolymorphicAction<ActionBase>>(
                new BlockMetadata(
                    index: 3,
                    timestamp: DateTimeOffset.MinValue,
                    publicKey: adminPublicKey,
                    previousHash: blockChain.Tip.Hash,
                    txHash: BlockContent<PolymorphicAction<ActionBase>>.DeriveTxHash(txs),
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)),
                transactions: txs).Propose().Evaluate(adminPrivateKey, blockChain);
            Assert.Throws<InvalidBlockTxCountException>(() => blockChain.Append(block3));
            Assert.Equal(3, blockChain.Count);
            Assert.False(blockChain.ContainsBlock(block3.Hash));
        }

        [Fact]
        public void ValidateNextBlockWithManyTransactionsPerSigner()
        {
            var adminPrivateKey = new PrivateKey();
            var adminPublicKey = adminPrivateKey.PublicKey;
            var blockPolicySource = new BlockPolicySource(Logger.None);
            IBlockPolicy<PolymorphicAction<ActionBase>> policy = blockPolicySource.GetPolicy(
                minimumDifficulty: 10_000,
                maxBlockBytesPolicy: null,
                minTransactionsPerBlockPolicy: null,
                maxTransactionsPerBlockPolicy: MaxTransactionsPerBlockPolicy
                    .Default
                    .Add(new SpannedSubPolicy<int>(0, null, null, 10)),
                maxTransactionsPerSignerPerBlockPolicy: MaxTransactionsPerSignerPerBlockPolicy
                    .Default
                    .Add(new SpannedSubPolicy<int>(2, null, null, 5)),
                authorizedMinersPolicy: null,
                permissionedMinersPolicy: null,
                validatorsPolicy: ValidatorsPolicy.Default
                    .Add(new SpannedSubPolicy<ValidatorSet>(
                        0,
                        null,
                        null,
                        new ValidatorSet(new List<PublicKey>
                        {
                            adminPublicKey,
                        }))));
            IStagePolicy<PolymorphicAction<ActionBase>> stagePolicy =
                new VolatileStagePolicy<PolymorphicAction<ActionBase>>();
            Block<PolymorphicAction<ActionBase>> genesis =
                MakeGenesisBlock(adminPublicKey.ToAddress(), ImmutableHashSet<Address>.Empty);

            using var store = new DefaultStore(null);
            var stateStore = new TrieStateStore(new MemoryKeyValueStore());
            var blockChain = new BlockChain<PolymorphicAction<ActionBase>>(
                policy,
                stagePolicy,
                store,
                stateStore,
                genesis
            );

            int nonce = 0;
            List<Transaction<PolymorphicAction<ActionBase>>> GenerateTransactions(int count)
            {
                var list = new List<Transaction<PolymorphicAction<ActionBase>>>();
                for (int i = 0; i < count; i++)
                {
                    list.Add(Transaction<PolymorphicAction<ActionBase>>.Create(
                        nonce++,
                        adminPrivateKey,
                        genesis.Hash,
                        new PolymorphicAction<ActionBase>[] { }
                    ));
                }

                return list;
            }

            BlockCommit GenerateBlockCommit(long height, BlockHash hash)
            {
                ImmutableArray<Vote> votes = ImmutableArray<Vote>.Empty
                    .Add(new VoteMetadata(
                        height, 0, hash, DateTimeOffset.UtcNow, adminPublicKey, VoteFlag.PreCommit)
                            .Sign(adminPrivateKey));
                return new BlockCommit(height, 0, hash, votes);
            }

            Assert.Equal(1, blockChain.Count);
            var txs = GenerateTransactions(10).OrderBy(tx => tx.Id).ToList();
            Block<PolymorphicAction<ActionBase>> block1 = new BlockContent<PolymorphicAction<ActionBase>>(
                new BlockMetadata(
                    index: 1,
                    timestamp: DateTimeOffset.MinValue,
                    publicKey: adminPublicKey,
                    previousHash: blockChain.Tip.Hash,
                    txHash: BlockContent<PolymorphicAction<ActionBase>>.DeriveTxHash(txs),
                    lastCommit: null),
                transactions: txs).Propose().Evaluate(adminPrivateKey, blockChain);

            // Should be fine since policy hasn't kicked in yet.
            blockChain.Append(block1);
            Assert.Equal(2, blockChain.Count);
            Assert.True(blockChain.ContainsBlock(block1.Hash));

            txs = GenerateTransactions(10).OrderBy(tx => tx.Id).ToList();
            Block<PolymorphicAction<ActionBase>> block2 = new BlockContent<PolymorphicAction<ActionBase>>(
                new BlockMetadata(
                    index: 2,
                    timestamp: DateTimeOffset.MinValue,
                    publicKey: adminPublicKey,
                    previousHash: blockChain.Tip.Hash,
                    txHash: BlockContent<PolymorphicAction<ActionBase>>.DeriveTxHash(txs),
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)),
                transactions: txs).Propose().Evaluate(adminPrivateKey, blockChain);

            // Subpolicy kicks in.
            Assert.Throws<InvalidBlockTxCountPerSignerException>(() => blockChain.Append(block2));
            Assert.Equal(2, blockChain.Count);
            Assert.False(blockChain.ContainsBlock(block2.Hash));
            // Since failed, roll back nonce.
            nonce -= 10;

            // Limit should also pass.
            txs = GenerateTransactions(5).OrderBy(tx => tx.Id).ToList();
            Block<PolymorphicAction<ActionBase>> block3 = new BlockContent<PolymorphicAction<ActionBase>>(
                new BlockMetadata(
                    index: 2,
                    timestamp: DateTimeOffset.MinValue,
                    publicKey: adminPublicKey,
                    previousHash: blockChain.Tip.Hash,
                    txHash: BlockContent<PolymorphicAction<ActionBase>>.DeriveTxHash(txs),
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)),
                transactions: txs).Propose().Evaluate(adminPrivateKey, blockChain);

            blockChain.Append(block3);
            Assert.Equal(3, blockChain.Count);
            Assert.True(blockChain.ContainsBlock(block3.Hash));
        }

        [Fact]
        public void PermissionedBlockPolicy()
        {
            // This creates genesis with _privateKey as its miner.
            var nonce = new byte[] { 0x00, 0x01, 0x02, 0x03 };
            var permissionedMinerKey = new PrivateKey();
            var nonPermissionedMinerKey = new PrivateKey();
            var pendingActivations = new[]
            {
                permissionedMinerKey,
                nonPermissionedMinerKey,
            }.Select(key => ActivationKey.Create(key, nonce).Item2).ToArray();

            Block<PolymorphicAction<ActionBase>> genesis = MakeGenesisBlock(
                default(Address),
                ImmutableHashSet<Address>.Empty,
                pendingActivations: pendingActivations);
            using var store = new DefaultStore(null);
            using var stateStore = new TrieStateStore(new DefaultKeyValueStore(null));
            var blockPolicySource = new BlockPolicySource(Logger.None);
            var blockChain = new BlockChain<PolymorphicAction<ActionBase>>(
                blockPolicySource.GetPolicy(
                    minimumDifficulty: 10_000,
                    maxBlockBytesPolicy: null,
                    minTransactionsPerBlockPolicy: null,
                    maxTransactionsPerBlockPolicy: null,
                    maxTransactionsPerSignerPerBlockPolicy: null,
                    authorizedMinersPolicy: null,
                    permissionedMinersPolicy: PermissionedMinersPolicy
                        .Default
                        .Add(new SpannedSubPolicy<ImmutableHashSet<Address>>(
                            startIndex: 1,
                            endIndex: null,
                            filter: null,
                            value: new Address[] { permissionedMinerKey.ToAddress() }
                                .ToImmutableHashSet())),
                    validatorsPolicy: ValidatorsPolicy.Default
                    .Add(new SpannedSubPolicy<ValidatorSet>(
                        0,
                        null,
                        null,
                        new ValidatorSet(new List<PublicKey>
                        {
                            permissionedMinerKey.PublicKey,
                        })))),
                new VolatileStagePolicy<PolymorphicAction<ActionBase>>(),
                store,
                stateStore,
                genesis,
                renderers: new[] { blockPolicySource.BlockRenderer }
            );

            BlockCommit GenerateBlockCommit(long height, BlockHash hash)
            {
                ImmutableArray<Vote> votes = ImmutableArray<Vote>.Empty
                    .Add(new VoteMetadata(
                        height, 0, hash, DateTimeOffset.UtcNow, permissionedMinerKey.PublicKey, VoteFlag.PreCommit)
                            .Sign(permissionedMinerKey));
                return new BlockCommit(height, 0, hash, votes);
            }

            // Old proof mining is still allowed.
            blockChain.StageTransaction(Transaction<PolymorphicAction<ActionBase>>.Create(
                0,
                permissionedMinerKey,
                genesis.Hash,
                new PolymorphicAction<ActionBase>[] { }
            ));
            blockChain.Append(blockChain.ProposeBlock(permissionedMinerKey));

            // Bad proof can also be mined.
            blockChain.StageTransaction(Transaction<PolymorphicAction<ActionBase>>.Create(
                0,
                nonPermissionedMinerKey,
                genesis.Hash,
                new PolymorphicAction<ActionBase>[] { }
            ));
            blockChain.Append(blockChain.ProposeBlock(
                permissionedMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            blockChain.Append(blockChain.ProposeBlock(
                permissionedMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Error, it isn't permissioned miner.
            Assert.Throws<BlockPolicyViolationException>(
                () => blockChain.Append(blockChain.ProposeBlock(
                    nonPermissionedMinerKey,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash))));
        }

        [Fact]
        public void MixedMiningPolicy()
        {
            var nonce = new byte[] { 0x00, 0x01, 0x02, 0x03 };
            var authorizedMinerKey = new PrivateKey();
            var permissionedMinerKey = new PrivateKey();
            var someMinerKey = new PrivateKey();
            var addresses = new Address[]
            {
                authorizedMinerKey.ToAddress(),
                permissionedMinerKey.ToAddress(),
                someMinerKey.ToAddress(),
            };
            var pendingActivations = new[]
            {
                authorizedMinerKey,
                permissionedMinerKey,
                someMinerKey,
            }.Select(key => ActivationKey.Create(key, nonce).Item2).ToArray();
            var action = new TransferAsset(
                new PrivateKey().ToAddress(),
                new PrivateKey().ToAddress(),
                new FungibleAssetValue(_currency, 0, 0));

            // This creates genesis with _privateKey as its miner.
            Block<PolymorphicAction<ActionBase>> genesis = MakeGenesisBlock(
                default(Address),
                ImmutableHashSet<Address>.Empty,
                pendingActivations: pendingActivations);
            using var store = new DefaultStore(null);
            using var stateStore = new TrieStateStore(new DefaultKeyValueStore(null));
            var blockPolicySource = new BlockPolicySource(Logger.None);
            var blockChain = new BlockChain<PolymorphicAction<ActionBase>>(
                blockPolicySource.GetPolicy(
                    minimumDifficulty: 10_000,
                    maxBlockBytesPolicy: null,
                    minTransactionsPerBlockPolicy: null,
                    maxTransactionsPerBlockPolicy: null,
                    maxTransactionsPerSignerPerBlockPolicy: null,
                    authorizedMinersPolicy: AuthorizedMinersPolicy
                        .Default
                        .Add(new SpannedSubPolicy<ImmutableHashSet<Address>>(
                            startIndex: 0,
                            endIndex: 6,
                            filter: index => index % 2 == 0,
                            value: new Address[] { authorizedMinerKey.ToAddress() }
                                .ToImmutableHashSet())),
                    permissionedMinersPolicy: PermissionedMinersPolicy
                        .Default
                        .Add(new SpannedSubPolicy<ImmutableHashSet<Address>>(
                            startIndex: 2,
                            endIndex: 10,
                            filter: index => index % 3 == 0,
                            value: new Address[] { permissionedMinerKey.ToAddress() }
                                .ToImmutableHashSet())),
                    validatorsPolicy: ValidatorsPolicy.Default
                    .Add(new SpannedSubPolicy<ValidatorSet>(
                        0,
                        null,
                        null,
                        new ValidatorSet(new List<PublicKey>
                        {
                            authorizedMinerKey.PublicKey,
                        })))),
                new VolatileStagePolicy<PolymorphicAction<ActionBase>>(),
                store,
                stateStore,
                genesis,
                renderers: new[] { blockPolicySource.BlockRenderer }
            );

            BlockCommit GenerateBlockCommit(long height, BlockHash hash)
            {
                ImmutableArray<Vote> votes = ImmutableArray<Vote>.Empty
                    .Add(new VoteMetadata(
                        height, 0, hash, DateTimeOffset.UtcNow, authorizedMinerKey.PublicKey, VoteFlag.PreCommit)
                            .Sign(authorizedMinerKey));
                return new BlockCommit(height, 0, hash, votes);
            }

            Transaction<PolymorphicAction<ActionBase>> proof;

            // Index 1: Anyone can mine.
            blockChain.Append(blockChain.ProposeBlock(someMinerKey));

            // Index 2: Only authorized miner can mine.
            Assert.Throws<BlockPolicyViolationException>(
                () => blockChain.Append(blockChain.ProposeBlock(
                    permissionedMinerKey,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash))));
            Assert.Throws<BlockPolicyViolationException>(
                () => blockChain.Append(blockChain.ProposeBlock(
                    someMinerKey,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash))));
            blockChain.Append(blockChain.ProposeBlock(
                authorizedMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 3: Only permissioned miner can mine.
            Assert.Throws<BlockPolicyViolationException>(
                () => blockChain.Append(blockChain.ProposeBlock(
                    authorizedMinerKey,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash))));
            Assert.Throws<BlockPolicyViolationException>(
                () => blockChain.Append(blockChain.ProposeBlock(
                    someMinerKey,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash))));
            blockChain.Append(blockChain.ProposeBlock(
                permissionedMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 4: Only authorized miner can mine.
            Assert.Throws<BlockPolicyViolationException>(
                () => blockChain.Append(blockChain.ProposeBlock(
                    permissionedMinerKey,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash))));
            Assert.Throws<BlockPolicyViolationException>(
                () => blockChain.Append(blockChain.ProposeBlock(
                    someMinerKey,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash))));
            blockChain.Append(blockChain.ProposeBlock(
                authorizedMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 5: Anyone can mine again.
            blockChain.Append(blockChain.ProposeBlock(
                someMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 6: In case both authorized mining and permissioned mining apply,
            // only authorized miner can mine.
            Assert.Throws<BlockPolicyViolationException>(
                () => blockChain.Append(blockChain.ProposeBlock(
                    permissionedMinerKey,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash))));
            blockChain.Append(blockChain.ProposeBlock(
                authorizedMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 7, 8, 9: Check authorized mining ended.
            blockChain.Append(blockChain.ProposeBlock(
                someMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
            blockChain.Append(blockChain.ProposeBlock(
                someMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
            Assert.Throws<BlockPolicyViolationException>(
                () => blockChain.Append(blockChain.ProposeBlock(
                    someMinerKey,
                    lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash))));
            proof = blockChain.MakeTransaction(
                permissionedMinerKey,
                new PolymorphicAction<ActionBase>[] { action });
            blockChain.Append(blockChain.ProposeBlock(
                permissionedMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 10, 11, 12: Check permissioned mining ended.
            blockChain.Append(blockChain.ProposeBlock(
                someMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
            blockChain.Append(blockChain.ProposeBlock(
                someMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
            blockChain.Append(blockChain.ProposeBlock(
                someMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));

            // Index 13, 14: Check authorized miner and permissioned miner can also mine
            // when policy is allowed for all miners.
            blockChain.Append(blockChain.ProposeBlock(
                authorizedMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
            blockChain.Append(blockChain.ProposeBlock(
                permissionedMinerKey,
                lastCommit: GenerateBlockCommit(blockChain.Tip.Index, blockChain.Tip.Hash)));
        }

        private Block<PolymorphicAction<ActionBase>> MakeGenesisBlock(
            Address adminAddress,
            IImmutableSet<Address> activatedAddresses,
            AuthorizedMinersState authorizedMinersState = null,
            DateTimeOffset? timestamp = null,
            PendingActivationState[] pendingActivations = null
        )
        {
            if (pendingActivations is null)
            {
                var nonce = new byte[] { 0x00, 0x01, 0x02, 0x03 };
                (ActivationKey activationKey, PendingActivationState pendingActivation) =
                    ActivationKey.Create(_privateKey, nonce);
                pendingActivations = new[] { pendingActivation };
            }

            var sheets = TableSheetsImporter.ImportSheets();
            return BlockHelper.ProposeGenesisBlock(
                sheets,
                new GoldDistribution[0],
                pendingActivations,
                new AdminState(adminAddress, 1500000),
                authorizedMinersState: authorizedMinersState,
                activatedAccounts: activatedAddresses,
                isActivateAdminAddress: false,
                credits: null,
                privateKey: _privateKey,
                timestamp: timestamp ?? DateTimeOffset.MinValue);
        }
    }
}
