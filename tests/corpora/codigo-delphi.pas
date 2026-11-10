unit ExemploRepositorio;

interface

uses
  System.SysUtils, System.Classes;

type
  TClienteService = class
  private
    FNomePadrao: string;
  public
    constructor Create(const ANome: string);
    function FormatarMensagem(const AValor: Double): string;
  end;

implementation

constructor TClienteService.Create(const ANome: string);
begin
	if Trim(ANome) = '' then
		FNomePadrao := 'Cliente Padrão'
	else
		FNomePadrao := ANome;
end;


function TClienteService.FormatarMensagem(const AValor: Double): string;
begin
  // Comentário com acentuação e aspas simples
  Result := Format('Cliente: "%s" - Saldo: R$ %.2f', [FNomePadrao, AValor]);
end;

end.
