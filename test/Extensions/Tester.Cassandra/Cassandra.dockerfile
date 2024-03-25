ARG CASSANDRAVERSION=4.1
FROM cassandra:${CASSANDRAVERSION}

RUN sed -i 's/auto_snapshot: true/auto_snapshot: false/g' /etc/cassandra/cassandra.yaml

# Disable virtual nodes
RUN sed -i -e "s/num_tokens/\#num_tokens/" /etc/cassandra/cassandra.yaml

# With virtual nodes disabled, we have to configure initial_token
RUN sed -i -e "s/\# initial_token:/initial_token: 0/" /etc/cassandra/cassandra.yaml
RUN echo "JVM_OPTS=\"\$JVM_OPTS -Dcassandra.initial_token=0\"" >> /etc/cassandra/cassandra-env.sh

# set 0.0.0.0 Listens on all configured interfaces
RUN sed -i -e "s/^rpc_address.*/rpc_address: 0.0.0.0/" /etc/cassandra/cassandra.yaml

# Be your own seed
RUN sed -i -e "s/- seeds: \"127.0.0.1\"/- seeds: \"$SEEDS\"/" /etc/cassandra/cassandra.yaml

# Disable gossip, no need in one node cluster
RUN echo "JVM_OPTS=\"\$JVM_OPTS -Dcassandra.skip_wait_for_gossip_to_settle=0\"" >> /etc/cassandra/cassandra-env.sh