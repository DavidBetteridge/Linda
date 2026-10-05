<?php

require_once __DIR__ . '/src/TupleSpaceClient.php';

use Tuples\TupleSpaceClient;

$client = new TupleSpaceClient('127.0.0.1', 8080, 'test-space');

try {
    echo "Sending tuple ('task', '1')...\n";
    $client->out('task', '1');

    echo "Reading tuple ('task', '1')...\n";
    $tuple = $client->rd('task', '1');
    echo "Read: " . implode(', ', $tuple) . "\n";

    echo "Consuming tuple ('task', '1')...\n";
    $tuple = $client->in('task', '1');
    echo "Consumed: " . implode(', ', $tuple) . "\n";

    echo "Probing for tuple ('task', '1') (should fail)...\n";
    $tuple = $client->inp('task', '1');
    if ($tuple === null) {
        echo "Correct: Tuple not found.\n";
    } else {
        echo "Error: Tuple found: " . implode(', ', $tuple) . "\n";
    }

    echo "Sending another tuple ('task', '2')...\n";
    $client->out('task', '2');

    echo "Probing for any task ('task', '*')...\n";
    $tuple = $client->rdp('task', '*');
    if ($tuple !== null) {
        echo "Found: " . implode(', ', $tuple) . "\n";
    } else {
        echo "Error: Tuple not found.\n";
    }

    echo "Test completed successfully.\n";

} catch (Exception $e) {
    echo "Test failed: " . $e->getMessage() . "\n";
    exit(1);
}
