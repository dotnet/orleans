#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
echo "=== Streaming Samples ==="
echo "Choose which streaming sample to run:"
echo "  1. Simple - Basic streaming with Orleans streams"
echo "  2. CustomDataAdapter - Custom data adapter for non-Orleans clients"
echo ""
read -p "Enter choice (1 or 2): " choice
case $choice in
    1) "$SCRIPT_DIR/Simple/run.sh" ;;
    2) "$SCRIPT_DIR/CustomDataAdapter/run.sh" ;;
    *) echo "Invalid choice. Running Simple sample by default..."
       "$SCRIPT_DIR/Simple/run.sh" ;;
esac
