using System;
using System.Collections.Generic;

namespace VmTyper.Sample
{
    public sealed class ProcessadorPedido
    {
        private readonly string _prefixo;

        public ProcessadorPedido(string prefixo = "PED")
        {
            _prefixo = prefixo ?? throw new ArgumentNullException(nameof(prefixo));
        }


        public string GerarResumo(int id, decimal valorTotal)
        {
        	if (id <= 0)
        	{
        		throw new ArgumentOutOfRangeException(nameof(id), "O identificador deve ser positivo.");
        	}

            return $"{_prefixo}-{id:D6}: R$ {valorTotal:N2} (status: \"aprovado\")";
        }
    }
}
