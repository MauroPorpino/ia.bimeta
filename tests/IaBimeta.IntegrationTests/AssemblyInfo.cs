// Os testes compartilham um único PostgreSQL de teste e sobem servidores locais: execução sequencial, sem surpresas.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
