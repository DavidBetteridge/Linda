using System;
using System.Security.Cryptography;

class Program {
    static void Main() {
        try {
            var mldsa = MLDsa.Create(MLDsaAlgorithmIdentifier.MLDsa65);
            Console.WriteLine("MLDsa created successfully");
        } catch (Exception e) {
            Console.WriteLine(e.Message);
        }
    }
}
