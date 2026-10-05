<?php

namespace Tuples;

use Exception;
use RuntimeException;

class TupleSpaceClient
{
    private $socket;
    private string $host;
    private int $port;
    private string $spaceName;

    public function __construct(string $host, int $port, string $spaceName)
    {
        $this->host = $host;
        $this->port = $port;
        $this->spaceName = $spaceName;
    }

    public function __destruct()
    {
        $this->close();
    }

    private function ensureConnected(): void
    {
        if ($this->socket === null) {
            $this->socket = fsockopen($this->host, $this->port, $errno, $errstr, 30);
            if (!$this->socket) {
                throw new RuntimeException("Could not connect to $this->host:$this->port: $errstr ($errno)");
            }
            
            // Create/Ensure space exists
            $this->sendCommand('CREATE', null);
        }
    }

    public function close(): void
    {
        if ($this->socket !== null) {
            fclose($this->socket);
            $this->socket = null;
        }
    }

    private function sendCommand(string $type, ?array $tuple): array
    {
        $this->ensureConnected();

        $command = [
            'Type' => $type,
            'SpaceName' => $this->spaceName,
            'Tuple' => $tuple
        ];

        $json = json_encode($command) . "\n";
        fwrite($this->socket, $json);

        $line = fgets($this->socket);
        if ($line === false) {
            $this->close();
            throw new RuntimeException("Disconnected from server");
        }

        $response = json_decode($line, true);
        if ($response === null) {
            throw new RuntimeException("Invalid response from server: " . $line);
        }

        return $response;
    }

    /**
     * Produce: Adds a tuple to the space.
     */
    public function out(string ...$tuple): array
    {
        $response = $this->sendCommand('OUT', $tuple);
        if (($response['Status'] ?? '') !== 'OK') {
            throw new Exception($response['Message'] ?? 'Unknown error');
        }
        return $tuple;
    }

    /**
     * Consume: Blocks until a tuple matching the pattern is available, then removes and returns it.
     */
    public function in(string ...$pattern): array
    {
        $response = $this->sendCommand('IN', $pattern);
        if (($response['Status'] ?? '') !== 'OK') {
            throw new Exception($response['Message'] ?? 'Unknown error');
        }
        return $response['Tuple'] ?? throw new Exception("No tuple returned");
    }

    /**
     * Read: Blocks until a tuple matching the pattern is available, then returns it without removing it.
     */
    public function rd(string ...$pattern): array
    {
        $response = $this->sendCommand('RD', $pattern);
        if (($response['Status'] ?? '') !== 'OK') {
            throw new Exception($response['Message'] ?? 'Unknown error');
        }
        return $response['Tuple'] ?? throw new Exception("No tuple returned");
    }

    /**
     * Probe Consume: Non-blocking version of IN.
     */
    public function inp(string ...$pattern): ?array
    {
        $response = $this->sendCommand('INP', $pattern);
        if (($response['Status'] ?? '') === 'OK') {
            return $response['Tuple'] ?? null;
        }
        return null;
    }

    /**
     * Probe Read: Non-blocking version of RD.
     */
    public function rdp(string ...$pattern): ?array
    {
        $response = $this->sendCommand('RDP', $pattern);
        if (($response['Status'] ?? '') === 'OK') {
            return $response['Tuple'] ?? null;
        }
        return null;
    }
}
